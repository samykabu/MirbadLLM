using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// The executable catalogue (design-p3p4 §A.1.2, §A.8; MN-222): <c>Agent:Executables</c> (logical name → installed
/// versions) verified against each version's install manifest (<c>manifest.json</c> in the version folder, written by
/// <c>install-agent.ps1 -LlamaCpp</c>, MN-216).
/// <list type="bullet">
/// <item><b>Every</b> file the manifest lists is hashed (SHA-256, size first): <c>llama-server.exe</c> is a 9 KB launcher and
/// the server code is <c>llama-server-impl.dll</c> and the other DLLs (spike S1 / C3; D120). The executable itself must be one
/// of the listed files.</item>
/// <item>Any <c>.exe</c>/<c>.dll</c> under the version folder that the manifest does not list is a mismatch (a planted DLL
/// would be loaded from the application folder first). On macOS (docs/macos/implementation-plan.md, Phase 2) so is an
/// unlisted <c>.dylib</c>, <c>.so</c> or Mach-O file of any name (the release resolves its dylibs through
/// <c>@rpath</c> = <c>@loader_path</c>, the version folder), and paths compare case-insensitively as on Windows (APFS is
/// case-insensitive by default). <c>manifest.json</c> is the one expected extra file: it is not an executable, and it is
/// never hashed as a listed file (a manifest that lists itself is refused).</item>
/// <item>A mismatch makes that version unverified (<see cref="ExecutableVersionState.Verified"/> false, health reason
/// <c>executable_unverified</c>, launches naming it 422 <c>executable_unverified</c>); other versions keep working.</item>
/// <item>The first full hash (≈ 712 MiB for <c>b11476</c>) runs in the background (<see cref="RefreshAsync"/> from
/// <see cref="ExecutableCatalogVerifier"/>, registered by the composition, MN-232), never before the listener opens
/// (§A.4.1). Until a version's first verification is done, <see cref="Resolve"/> answers 422 <c>executable_unverified</c>
/// ("being verified"; the master retries) and starts the verification itself if nothing has.</item>
/// <item>After that, <see cref="Resolve"/> re-hashes synchronously only when something changed since the last check: a
/// listed file's size or modification time, the manifest's, or the set of <c>.exe</c>/<c>.dll</c> files in the folder. Only
/// the changed files are hashed again. <see cref="List"/> notices the same changes and re-verifies in the background.</item>
/// </list>
/// Thread-safe. Messages name files relative to the version folder, never file contents.
/// </summary>
public sealed partial class ExecutableCatalog : IExecutableCatalog
{
    /// <summary><see cref="ExecutableVersionState.Problem"/> of a version whose first verification has not finished.</summary>
    public const string NotVerifiedYet = "not verified yet: its files are being hashed";

    /// <summary><see cref="ExecutableVersionState.Problem"/> of a version whose files changed and are being hashed again.</summary>
    public const string ReVerifying = "its files changed since the last verification and are being hashed again";

    /// <summary>The manifest's file name in a version folder.</summary>
    public const string ManifestFileName = "manifest.json";

    // An MLX version folder (a virtualenv with its own Python, compiled bytecode included) holds ~15k files.
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private const int MaxManifestFiles = 100_000;

    private readonly List<Entry> _entries = [];
    private readonly ILogger _logger;
    private readonly ExecutablePlatform _platform;
    private readonly StringComparer _pathComparer;

    /// <summary>Creates the catalogue over <c>Agent:Executables</c>. Nothing is read or hashed here.</summary>
    /// <param name="executables">The configured logical executables and their versions.</param>
    /// <param name="logger">Verification results (file names only).</param>
    public ExecutableCatalog(IEnumerable<ExecutableOptions> executables, ILogger<ExecutableCatalog>? logger = null)
        : this(executables, logger, CurrentPlatform)
    {
    }

    /// <summary>Creates the catalogue with the version-folder rules of <paramref name="platform"/> (tests use the macOS rules on any OS).</summary>
    /// <param name="executables">The configured logical executables and their versions.</param>
    /// <param name="logger">Verification results (file names only).</param>
    /// <param name="platform">Which files count as executable and how paths compare.</param>
    internal ExecutableCatalog(IEnumerable<ExecutableOptions> executables, ILogger<ExecutableCatalog>? logger, ExecutablePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(executables);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _platform = platform;
        _pathComparer = platform is ExecutablePlatform.Windows or ExecutablePlatform.MacOS ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var executable in executables)
        {
            if (executable is null || string.IsNullOrWhiteSpace(executable.Name))
            {
                continue;
            }

            string? defaultVersion = executable.Default ?? (executable.Versions.Count == 1 ? executable.Versions[0].Version : null);
            foreach (var version in executable.Versions)
            {
                if (version is null || string.IsNullOrWhiteSpace(version.Version))
                {
                    continue;
                }

                if (_entries.Any(e => e.Name == executable.Name && e.Version == version.Version))
                {
                    LogDuplicate(_logger, executable.Name, version.Version);
                    continue;
                }

                _entries.Add(new Entry(executable.Name, version, string.Equals(version.Version, defaultVersion, StringComparison.Ordinal)));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExecutableVersionState> List()
    {
        var states = new List<ExecutableVersionState>(_entries.Count);
        foreach (var entry in _entries)
        {
            VerificationResult? result = entry.Result;
            string? problem;
            bool verified;
            if (result is null)
            {
                StartBackground(entry, full: true);
                (verified, problem) = (false, NotVerifiedYet);
            }
            else if (!string.Equals(Snapshot(entry, result.Manifest), result.Stamp, StringComparison.Ordinal))
            {
                StartBackground(entry, full: false);
                (verified, problem) = (false, ReVerifying);
            }
            else
            {
                (verified, problem) = (result.Verified, result.Problem);
            }

            states.Add(new ExecutableVersionState(entry.Name, entry.Version, entry.Options.Cuda, verified, entry.IsDefault, problem));
        }

        return states;
    }

    /// <inheritdoc />
    public ResolvedExecutable Resolve(string name, string? version)
    {
        var entry = Find(name, version);
        VerificationResult? result = entry.Result;
        if (result is null)
        {
            StartBackground(entry, full: true);
            throw Unverified(entry, NotVerifiedYet);
        }

        if (!string.Equals(Snapshot(entry, result.Manifest), result.Stamp, StringComparison.Ordinal))
        {
            if (!entry.Gate.Wait(0))
            {
                throw Unverified(entry, ReVerifying);
            }

            try
            {
                result = Verify(entry, full: false, CancellationToken.None);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        return result.Verified
            ? new ResolvedExecutable(entry.Name, entry.Version, result.ExecutablePath!, result.Folder!, entry.Options.Cuda)
            : throw Unverified(entry, result.Problem ?? "verification failed");
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        long requested = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var entry in _entries)
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (entry.Result is { Full: true } done && done.StartedAt >= requested)
                {
                    continue; // a full verification that began after this call (e.g. started by the first List) just ran
                }

                await Task.Run(() => Verify(entry, full: true, ct), ct).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    /// <summary>The version-folder rules of the OS the agent runs on.</summary>
    internal static ExecutablePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ExecutablePlatform.Windows : OperatingSystem.IsMacOS() ? ExecutablePlatform.MacOS : ExecutablePlatform.Other;

    /// <summary>Completes when no background verification is running (tests).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_entries.Select(e => e.Background ?? Task.CompletedTask));

    private Entry Find(string name, string? version)
    {
        ArgumentNullException.ThrowIfNull(name);
        var named = _entries.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
        Entry? entry = version is null ? named.FirstOrDefault(e => e.IsDefault) : named.FirstOrDefault(e => string.Equals(e.Version, version, StringComparison.Ordinal));
        if (entry is not null)
        {
            return entry;
        }

        string installed = named.Count == 0
            ? $"installed executables: {Names(_entries.Select(e => e.Name).Distinct(StringComparer.Ordinal))}"
            : $"installed versions: {Names(named.Select(e => e.Version))}";
        string what = version is null ? $"Executable '{name}' has no default version on this node" : $"Executable '{name}' version '{version}' is not installed on this node";
        throw new AgentException(422, new AgentError { Code = ErrorCodes.ExecutableMissing, Message = $"{what} ({installed})." });

        static string Names(IEnumerable<string> names)
        {
            string joined = string.Join(", ", names);
            return joined.Length == 0 ? "none" : joined;
        }
    }

    private AgentException Unverified(Entry entry, string problem) => new(422, new AgentError
    {
        Code = ErrorCodes.ExecutableUnverified,
        Message = $"Executable '{entry.Name}' {entry.Version} is not verified: {problem}. Reinstall it with {ReinstallHint(entry.Name)} if this persists.",
    });

    /// <summary>The installer command that re-provisions <paramref name="name"/> on this OS.</summary>
    private string ReinstallHint(string name) => _platform == ExecutablePlatform.MacOS
        ? string.Equals(name, ExecutableNames.MlxLmServer, StringComparison.Ordinal) ? "install-agent.sh --update-mlx" : "install-agent.sh --update-llama"
        : "install-agent.ps1 -LlamaCpp";

    private void StartBackground(Entry entry, bool full)
    {
        if (!entry.Gate.Wait(0))
        {
            return; // a verification is running; it will store a fresh result
        }

        entry.Background = Task.Run(() =>
        {
            try
            {
                Verify(entry, full, CancellationToken.None);
            }
#pragma warning disable CA1031 // a background verification never takes the agent down; Verify reports file problems itself
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogVerifyCrashed(_logger, ex, entry.Name, entry.Version);
            }
            finally
            {
                entry.Gate.Release();
            }
        });
    }

    /// <summary>Verifies one version (the caller holds its gate) and stores the result.</summary>
    private VerificationResult Verify(Entry entry, bool full, CancellationToken ct)
    {
        VerificationResult? previous = entry.Result;
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        VerificationResult result;
        try
        {
            result = VerifyCore(entry, full ? null : previous, ct) with { Full = full, StartedAt = startedAt };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // any unexpected failure leaves the version unverified with the reason, never verified
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new VerificationResult(false, "verification failed: " + ex.Message, Snapshot(entry, null), null, null, null, new Dictionary<string, string>(_pathComparer))
            {
                Full = full,
                StartedAt = startedAt,
            };
        }

        entry.Result = result;
        if (previous is null || previous.Verified != result.Verified || !string.Equals(previous.Problem, result.Problem, StringComparison.Ordinal))
        {
            if (result.Verified)
            {
                LogVerified(_logger, entry.Name, entry.Version, result.Manifest!.Files.Count);
            }
            else
            {
                LogUnverified(_logger, entry.Name, entry.Version, result.Problem!);
            }
        }

        return result;
    }

    private VerificationResult VerifyCore(Entry entry, VerificationResult? previous, CancellationToken ct)
    {
        var hashed = new Dictionary<string, string>(_pathComparer);
        string? folder = null;
        string? executablePath = null;
        string manifestPath = entry.Options.Manifest;
        if (!Path.IsPathFullyQualified(manifestPath) || !Path.IsPathFullyQualified(entry.Options.Path))
        {
            return Fail("the configured executable and manifest paths must be absolute", null);
        }

        manifestPath = Path.GetFullPath(manifestPath);
        folder = Path.GetDirectoryName(manifestPath)!;
        executablePath = Path.GetFullPath(entry.Options.Path);
        string? executableRelative = Relative(folder, executablePath);
        if (executableRelative is null)
        {
            return Fail("the executable is not inside the manifest's folder", null);
        }

        // The stamp is taken before anything is read: a file that changes while it is hashed is re-hashed at the next check.
        ExecutableManifest manifest;
        try
        {
            manifest = ReadManifest(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            return Fail($"{ManifestFileName} cannot be read ({ex.Message})", null);
        }

        string stampBefore = Snapshot(entry, manifest);
        if (CheckManifest(entry, manifest, executableRelative) is { } invalid)
        {
            return Fail(invalid, manifest, stampBefore);
        }

        if (UnlistedExecutables(folder, manifest).FirstOrDefault() is { } extra)
        {
            return Fail(manifest.AllFiles ? $"{extra}: a file the manifest does not list" : $"{extra}: an executable file the manifest does not list",
                manifest, stampBefore);
        }

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string? stamp = FileStamp(path);
            if (stamp is null)
            {
                return Fail($"{file.Path}: missing", manifest, stampBefore);
            }

            if (previous?.Hashed.TryGetValue(file.Path, out string? known) == true && string.Equals(known, stamp, StringComparison.Ordinal) &&
                previous.Manifest is not null && previous.Manifest.Files.Any(f => _pathComparer.Equals(f.Path, file.Path) &&
                    string.Equals(f.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) && f.Bytes == file.Bytes))
            {
                hashed[file.Path] = stamp; // unchanged since it last matched this very manifest entry
                continue;
            }

            if (HashProblem(path, file, ct) is { } problem)
            {
                return Fail($"{file.Path}: {problem}", manifest, stampBefore);
            }

            hashed[file.Path] = stamp;
        }

        return new VerificationResult(true, null, stampBefore, manifest, folder, executablePath, hashed);

        VerificationResult Fail(string problem, ExecutableManifest? m, string? stamp = null) =>
            new(false, problem, stamp ?? Snapshot(entry, m), m, folder, executablePath, hashed);
    }

    private static ExecutableManifest ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxManifestBytes)
        {
            throw new InvalidDataException($"larger than {MaxManifestBytes} bytes");
        }

        return JsonSerializer.Deserialize<ExecutableManifest>(stream, AgentJson.Options) ?? throw new InvalidDataException("empty");
    }

    private string? CheckManifest(Entry entry, ExecutableManifest manifest, string executableRelative)
    {
        if (manifest.SchemaVersion != 1)
        {
            return $"{ManifestFileName} has schema version {manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)} (expected 1)";
        }

        if (!string.Equals(manifest.Name, entry.Name, StringComparison.Ordinal) || !string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal))
        {
            return $"{ManifestFileName} describes {manifest.Name} {manifest.Version}, not {entry.Name} {entry.Version}";
        }

        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > MaxManifestFiles)
        {
            return $"{ManifestFileName} lists no files (or too many)";
        }

        var seen = new HashSet<string>(_pathComparer);
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsPlainRelative(file.Path))
            {
                return $"{ManifestFileName} lists an invalid path '{file?.Path}'";
            }

            if (!seen.Add(file.Path))
            {
                return $"{ManifestFileName} lists '{file.Path}' twice";
            }

            if (_pathComparer.Equals(file.Path, ManifestFileName))
            {
                return $"{ManifestFileName} lists itself";
            }

            if (file.Bytes < 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
            {
                return $"{ManifestFileName} has an invalid size or SHA-256 for '{file.Path}'";
            }
        }

        return seen.Contains(executableRelative) ? null : $"the executable {executableRelative} is not listed in {ManifestFileName}";
    }

    /// <summary>A <c>/</c>-separated path inside the folder: no root, drive, stream, <c>..</c>/<c>.</c> or empty segment.</summary>
    private static bool IsPlainRelative(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 512 && !path.Contains('\\', StringComparison.Ordinal) &&
        !path.Contains(':', StringComparison.Ordinal) && !path.Contains('\0', StringComparison.Ordinal) && !Path.IsPathRooted(path) &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." && segment.Trim().Length == segment.Length);

    /// <summary>Size first, then SHA-256; null when the file matches.</summary>
    private static string? HashProblem(string path, ExecutableManifestFile file, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 0,
                Options = FileOptions.SequentialScan,
            });
            if (stream.Length != file.Bytes)
            {
                return "size mismatch";
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
            }

            string actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            return string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "hash mismatch";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable (" + ex.Message + ")";
        }
    }

    /// <summary>
    /// Every executable file (<see cref="IsExecutableFile"/>) under the folder (recursively) that the manifest does not list,
    /// relative and sorted; with <see cref="ExecutableManifest.AllFiles"/>, every unlisted file of any kind
    /// (<c>manifest.json</c> aside).
    /// </summary>
    private IEnumerable<string> UnlistedExecutables(string folder, ExecutableManifest? manifest)
    {
        var listed = new HashSet<string>(manifest?.Files.Where(f => f is not null).Select(f => f.Path) ?? [], _pathComparer);
        return ExecutableFiles(folder, manifest?.AllFiles == true)
            .Where(relative => !listed.Contains(relative) && !_pathComparer.Equals(relative, ManifestFileName));
    }

    private List<string> ExecutableFiles(string folder, bool allFiles = false)
    {
        var found = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0, MatchType = MatchType.Simple };
            foreach (string path in Directory.EnumerateFiles(folder, "*", options))
            {
                // With a complete listing every file counts, so no header needs reading (a virtualenv has ~15k files).
                if (allFiles || IsExecutableFile(path))
                {
                    found.Add(Relative(folder, path) ?? path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found.Add("(folder unreadable: " + ex.Message + ")");
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Whether a file in a version folder is code a loader could pick up: <c>.exe</c>/<c>.dll</c> everywhere; on macOS also
    /// <c>.dylib</c>, <c>.so</c> and any file that starts with a Mach-O or universal (fat) magic number, whatever its name.
    /// </summary>
    private bool IsExecutableFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_platform != ExecutablePlatform.MacOS)
        {
            return false;
        }

        return extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase) || extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
            StartsWithMachOMagic(path);
    }

    /// <summary>
    /// True when the file's first 4 bytes are a Mach-O magic (32 or 64 bit, either byte order) or a universal binary's.
    /// A file that exists but cannot be read counts as one (fail closed: the installer leaves every file world-readable).
    /// </summary>
    internal static bool StartsWithMachOMagic(string path)
    {
        Span<byte> header = stackalloc byte[4];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // deleted since the folder was listed: nothing left to load
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        return magic is 0xFEEDFACE or 0xCEFAEDFE // Mach-O 32-bit (MH_MAGIC / MH_CIGAM)
            or 0xFEEDFACF or 0xCFFAEDFE // Mach-O 64-bit (MH_MAGIC_64 / MH_CIGAM_64)
            or 0xCAFEBABE or 0xBEBAFECA // universal (FAT_MAGIC / FAT_CIGAM)
            or 0xCAFEBABF or 0xBFBAFECA; // universal with 64-bit offsets (FAT_MAGIC_64 / FAT_CIGAM_64)
    }

    /// <summary>
    /// What a change check compares: the manifest's and every listed file's size and modification time, plus the
    /// executable files present (<see cref="IsExecutableFile"/>). Cheap (metadata only; on macOS also a 4-byte header read
    /// per file without a library extension).
    /// </summary>
    private string Snapshot(Entry entry, ExecutableManifest? manifest)
    {
        var text = new StringBuilder();
        string manifestPath = Path.IsPathFullyQualified(entry.Options.Manifest) ? Path.GetFullPath(entry.Options.Manifest) : entry.Options.Manifest;
        text.Append("m|").Append(FileStamp(manifestPath) ?? "-").Append('\n');
        string? folder = Path.IsPathFullyQualified(manifestPath) ? Path.GetDirectoryName(manifestPath) : null;
        if (folder is null)
        {
            return text.ToString();
        }

        if (manifest?.Files is { } files)
        {
            foreach (var file in files)
            {
                if (file is not null && IsPlainRelative(file.Path))
                {
                    text.Append("f|").Append(file.Path).Append('|')
                        .Append(FileStamp(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar))) ?? "-").Append('\n');
                }
            }
        }

        foreach (string executable in ExecutableFiles(folder, manifest?.AllFiles == true))
        {
            text.Append("x|").Append(executable).Append('\n');
        }

        return text.ToString();
    }

    private static string? FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}|{info.LastWriteTimeUtc.Ticks}")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><paramref name="path"/> relative to <paramref name="folder"/>, <c>/</c>-separated; null when outside it.</summary>
    private static string? Relative(string folder, string path)
    {
        string relative = Path.GetRelativePath(folder, path);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    [LoggerMessage(EventId = 2140, Level = LogLevel.Information, Message = "Executable {Name} {Version} verified ({Files} files match its manifest).")]
    private static partial void LogVerified(ILogger logger, string name, string version, int files);

    [LoggerMessage(EventId = 2141, Level = LogLevel.Warning, Message = "Executable {Name} {Version} is unverified: {Problem}. Launches naming it get 422 executable_unverified.")]
    private static partial void LogUnverified(ILogger logger, string name, string version, string problem);

    [LoggerMessage(EventId = 2142, Level = LogLevel.Warning, Message = "Executable {Name} {Version} is configured twice; the first entry is used.")]
    private static partial void LogDuplicate(ILogger logger, string name, string version);

    [LoggerMessage(EventId = 2143, Level = LogLevel.Error, Message = "Background verification of executable {Name} {Version} failed.")]
    private static partial void LogVerifyCrashed(ILogger logger, Exception exception, string name, string version);

    private sealed class Entry(string name, ExecutableVersionOptions options, bool isDefault)
    {
        private VerificationResult? _result;
        private Task? _background;

        public string Name { get; } = name;

        public string Version { get; } = options.Version;

        public ExecutableVersionOptions Options { get; } = options;

        public bool IsDefault { get; } = isDefault;

        /// <summary>Held while this version is verified (one verification at a time).</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public VerificationResult? Result
        {
            get => Volatile.Read(ref _result);
            set => Volatile.Write(ref _result, value);
        }

        public Task? Background
        {
            get => Volatile.Read(ref _background);
            set => Volatile.Write(ref _background, value);
        }
    }

    /// <param name="Verified">Every listed file matched and nothing unlisted is executable.</param>
    /// <param name="Problem">The first problem found; null when verified.</param>
    /// <param name="Stamp">The <see cref="Snapshot"/> taken before the files were read.</param>
    /// <param name="Manifest">The manifest read, when readable.</param>
    /// <param name="Folder">The version folder.</param>
    /// <param name="ExecutablePath">The executable's full path.</param>
    /// <param name="Hashed">Listed files that matched, with the size|mtime stamp they had (incremental re-checks).</param>
    private sealed record VerificationResult(
        bool Verified, string? Problem, string Stamp, ExecutableManifest? Manifest, string? Folder, string? ExecutablePath,
        IReadOnlyDictionary<string, string> Hashed)
    {
        /// <summary>True when every listed file was hashed (no stamps reused).</summary>
        public bool Full { get; init; }

        /// <summary><see cref="System.Diagnostics.Stopwatch"/> timestamp of the verification's start.</summary>
        public long StartedAt { get; init; }
    }
}

/// <summary>
/// Runs the catalogue's first full verification in the background once the host has started (design-p3p4 §A.4.1: the
/// ≈ 712 MiB hash never delays the listener). Registered by the composition on a process node (MN-232).
/// </summary>
public sealed partial class ExecutableCatalogVerifier(IExecutableCatalog catalog, ILogger<ExecutableCatalogVerifier>? logger = null) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await catalog.RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during the first hash: the next start hashes again.
        }
#pragma warning disable CA1031 // the agent keeps running; the versions stay unverified and Resolve retries
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed((ILogger?)logger ?? NullLogger.Instance, ex);
        }
    }

    [LoggerMessage(EventId = 2144, Level = LogLevel.Error, Message = "The executable catalogue's first verification failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Which version-folder rules <see cref="ExecutableCatalog"/> applies: what counts as an executable file the manifest must
/// list, and whether paths compare case-insensitively.
/// </summary>
internal enum ExecutablePlatform
{
    /// <summary>Linux and anything else: <c>.exe</c>/<c>.dll</c>, case-sensitive paths.</summary>
    Other,

    /// <summary>Windows: <c>.exe</c>/<c>.dll</c>, case-insensitive paths (NTFS).</summary>
    Windows,

    /// <summary>macOS: also <c>.dylib</c>, <c>.so</c> and Mach-O files of any name; case-insensitive paths (APFS default).</summary>
    MacOS,
}
