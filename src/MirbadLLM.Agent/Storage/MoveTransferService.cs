using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Pki;
using OperationStatus = MirbadLLM.Agent.Contracts.OperationStatus;

namespace MirbadLLM.Agent.Storage;

/// <summary>Owns the durable state and in-memory leases of the node's scoped model copies.</summary>
public sealed class MoveTransferService(
    ModelRootPaths paths,
    StorageOperations operations,
    IAgentCredentials credentials,
    AgentHostOptions hostOptions,
    IOptions<AgentStorageOptions> options,
    IDiskSpaceProbe disk,
    IAgentDownloads downloads,
    IEnumerable<IRuntimeDriver> drivers,
    TimeProvider time) : IAsyncDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dataSlot = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _journalRoot = Path.Combine(hostOptions.StateDirectory, "move-transfers");

    internal Func<CancellationToken, Task>? BeforeMergeForTests { get; set; }

    public async Task<MoveTransferStatus> OpenAsync(MoveTransferOpenRequest request, OperationStatus accepted, MasterTriple master,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Role) || ModelRootPaths.NameError(request.Folder) is not null ||
            RepoPathRules.Check(request.Folder) is not null || request.Folder.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
            request.PlacementGeneration < 0 || string.IsNullOrWhiteSpace(request.MoveId) || string.IsNullOrWhiteSpace(request.ModelId) ||
            string.IsNullOrWhiteSpace(request.SourceUid) || string.IsNullOrWhiteSpace(request.TargetUid) || request.SourceUid == request.TargetUid)
        {
            throw StorageErrors.InvalidRequest("The transfer's role, folder, placement or audience is invalid.");
        }

        if ((request.Role == MoveTransferRole.Export ? request.SourceUid : request.TargetUid) != credentials.NodeUid)
        {
            throw Error(ErrorCodes.Forbidden, "This node is outside the transfer audience.");
        }

        long total = 0;
        if (request.Role == MoveTransferRole.Import)
        {
            if (request.Manifest is null || request.ManifestId != request.Manifest.ManifestId)
            {
                throw StorageErrors.InvalidRequest("An import requires its pinned manifest.");
            }

            try
            {
                total = MoveManifestRules.Validate(request.Manifest);
            }
            catch (InvalidDataException ex)
            {
                throw StorageErrors.InvalidRequest(ex.Message);
            }
        }
        else if (request.Manifest is not null || request.ManifestId is not null && !ContentDigest.IsHex(request.ManifestId, 64))
        {
            throw StorageErrors.InvalidRequest("An export accepts only an optional pinned manifest ID.");
        }

        EnsureCurrent(master);
        var transferId = IdOf(accepted.Id);
        var scope = request with { Manifest = null };
        var journal = new TransferJournal(scope, MoveTransferPhase.Preparing, request.Manifest, total);
        var entry = _entries.GetOrAdd(transferId, _ => new Entry(transferId, journal));
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Save(entry, journal);
            await operations.StageAsync(accepted.Id, new OperationStageUpdate
            {
                State = OperationState.Running, Stage = OperationStages.Downloading,
            }, ct).ConfigureAwait(false);
            return Status(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public MoveTransferStatus Get(string transferId) => Status(Find(transferId));

    public static string IdOf(string operationId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))[..32];

    public async Task<MoveTransferLease> LeaseAsync(string transferId, MasterTriple master, CancellationToken ct)
    {
        var entry = Find(transferId);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureCurrent(master);
            if (entry.Journal.Phase is MoveTransferPhase.Failed or MoveTransferPhase.Cancelled)
            {
                throw Error(ErrorCodes.FolderConflict, "This transfer has ended.");
            }

            if (entry.Journal.Phase != MoveTransferPhase.Completed)
            {
                await ReserveAsync(entry, ct).ConfigureAwait(false);
            }

            byte[] grant = RandomNumberGenerator.GetBytes(32);
            var expires = time.GetUtcNow() + LeaseDuration;
            var lease = new LeaseState(SHA256.HashData(grant), master, expires, new CancellationTokenSource(LeaseDuration, time));
            var previous = Interlocked.Exchange(ref entry.Lease, lease);
            if (previous is not null)
            {
                await previous.Lifetime.CancelAsync().ConfigureAwait(false);
            }

            _ = ExpireAsync(entry, lease);
            if ((entry.Journal.Phase is MoveTransferPhase.Preparing or MoveTransferPhase.Verifying) && entry.Preparation.IsCompleted)
            {
                StartWorker(entry, master);
            }

            return new MoveTransferLease(Convert.ToHexStringLower(grant), expires);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void StartWorker(Entry entry, MasterTriple master) =>
        entry.Preparation = Task.Run(() => PrepareAsync(entry, master, _shutdown.Token), CancellationToken.None);

    private async Task ExpireAsync(Entry entry, LeaseState lease)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lease.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Interlocked.CompareExchange(ref entry.Lease, null, lease) == lease)
                {
                    paths.ReleaseTransfer(entry.Journal.Scope.Folder, entry.Id, entry.Journal.Scope.Role);
                }
            }
            finally
            {
                entry.Gate.Release();
                lease.Lifetime.Dispose();
            }
        }
    }

    private async Task ReserveAsync(Entry entry, CancellationToken ct)
    {
        var scope = entry.Journal.Scope;
        var gate = paths.Gate(scope.Folder);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await downloads.IsFolderInUseAsync(scope.Folder, ct).ConfigureAwait(false))
            {
                throw Error(ErrorCodes.FolderConflict, "A download owns the transfer folder.");
            }

            if (scope.Role == MoveTransferRole.Import)
            {
                foreach (var driver in drivers)
                {
                    if (await driver.IsFolderInUseAsync(scope.Folder, ct).ConfigureAwait(false))
                    {
                        throw Error(ErrorCodes.FolderConflict, "A runtime uses the import folder.");
                    }
                }
            }

            var location = paths.Locate(scope.Folder);
            if (location.IsLink || scope.Role == MoveTransferRole.Export &&
                (Directory.Exists(location.Path + ".partial") || File.Exists(location.Path) ||
                 location.Exists && ReadNativeManifest(scope.Folder) is { State: not DownloadManifestState.Completed }))
            {
                throw Error(ErrorCodes.FolderConflict, "The checkpoint folder is linked, partial or unavailable.");
            }

            if (scope.Role == MoveTransferRole.Export && !location.Exists)
            {
                throw StorageErrors.NotFound($"Checkpoint folder '{scope.Folder}' is missing on the source.");
            }

            if (!paths.ReserveTransfer(scope.Folder, entry.Id, scope.Role))
            {
                throw Error(ErrorCodes.FolderConflict, "Another transfer owns the checkpoint folder.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private DownloadManifest? ReadNativeManifest(string folder)
    {
        using var file = ConfinedModelFile.OpenManifest(paths.Root, folder);
        return file is null ? null : DownloadManifest.Read(file);
    }

    private async Task PrepareAsync(Entry entry, MasterTriple master, CancellationToken ct)
    {
        var journal = entry.Journal;
        try
        {
            void Check()
            {
                ct.ThrowIfCancellationRequested();
                var lease = Volatile.Read(ref entry.Lease);
                if (lease is null || lease.Expires <= time.GetUtcNow() || lease.Lifetime.IsCancellationRequested ||
                    lease.Master.InstanceId != master.InstanceId || !operations.Fencing.IsCurrent(lease.Master))
                {
                    throw new OperationCanceledException("The checkpoint transfer lease ended.");
                }
            }

            Check();
            if (journal.Phase == MoveTransferPhase.Verifying)
            {
                if (journal.Scope.Role == MoveTransferRole.Export)
                {
                    VerifyExport(journal, Check, ct);
                }
                else
                {
                    await PublishAsync(entry, Check, ct).ConfigureAwait(false);
                }

                await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    Check();
                    await EndAsync(entry, MoveTransferPhase.Completed, null).ConfigureAwait(false);
                }
                finally
                {
                    entry.Gate.Release();
                }
                return;
            }

            var prepared = journal.Scope.Role == MoveTransferRole.Export
                ? await BuildExportAsync(journal, Check, ct).ConfigureAwait(false)
                : await PrepareImportAsync(entry, journal, Check, ct).ConfigureAwait(false);
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Check();
                Save(entry, prepared with { Phase = MoveTransferPhase.Ready });
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A new lease can start preparation again. Durable offsets are never advanced here on cancellation.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or AgentException)
        {
            await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (entry.Journal.Phase is MoveTransferPhase.Preparing or MoveTransferPhase.Verifying)
                {
                    var error = ex is AgentException agent ? agent.Error : StorageErrors.Error(ErrorCodes.FolderConflict,
                        ex is InvalidDataException ? ex.Message : "Could not read a confined checkpoint file or prepare its staging folder.");
                    await EndAsync(entry, MoveTransferPhase.Failed, error).ConfigureAwait(false);
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private async Task<TransferJournal> BuildExportAsync(TransferJournal journal, Action check, CancellationToken ct)
    {
        string folder = journal.Scope.Folder;
        string root = paths.Root;
        var names = MoveManifestRules.EnumeratePayload(Path.Combine(root, folder), ct);
        MoveManifestRules.ValidateSplitGguf(names);
        var named = names.ToHashSet(StringComparer.Ordinal);
        var files = new List<MoveManifestFile>(names.Count);
        var identities = new List<ConfinedModelFile.Identity>(names.Count);
        foreach (string name in names)
        {
            check();
            await using var file = OpenRead(root, folder + "/" + name);
            var before = Identify(file);
            using var git = GitHash(before.Size);
            string hash = await HashAsync(file, check, ct, git).ConfigureAwait(false);
            await CheckIndexAsync(name, file, named, ct).ConfigureAwait(false);
            if (Identify(file) != before)
            {
                throw Changed();
            }

            files.Add(new MoveManifestFile(name, before.Size, hash) { GitBlobSha1 = Convert.ToHexStringLower(git.GetHashAndReset()) });
            identities.Add(before);
        }

        var manifest = new MoveFolderManifest(1, files, MoveFolderManifest.IdFor(files));
        if (journal.Scope.ManifestId is { } pinned && pinned != manifest.ManifestId ||
            !names.SequenceEqual(MoveManifestRules.EnumeratePayload(Path.Combine(root, folder), ct)))
        {
            throw Changed();
        }

        return journal with { Manifest = manifest, TotalBytes = MoveManifestRules.Validate(manifest), Identities = identities };
    }

    private async Task<TransferJournal> PrepareImportAsync(Entry entry, TransferJournal journal, Action check, CancellationToken ct)
    {
        var manifest = journal.Manifest!;
        string root = paths.Root;
        string stage = Stage(entry, root);
        var location = paths.Locate(journal.Scope.Folder);
        if (location.IsLink || RepoPathRules.IsLink(new DirectoryInfo(stage)))
        {
            throw Error(ErrorCodes.FolderConflict, "The destination or its staging directory is linked.");
        }

        ValidateDestination(location, manifest, ct);
        var offsets = journal.Offsets?.ToArray() ?? new long[manifest.Files.Count];
        var reused = new bool[manifest.Files.Count];
        long remaining = 0;
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            check();
            var file = manifest.Files[i];
            if (await CheckExistingAsync(root, location, file, check, ct).ConfigureAwait(false))
            {
                reused[i] = true;
                offsets[i] = file.Size;
                continue;
            }

            remaining += file.Size - offsets[i];
        }

        if (remaining > 0 && (disk.Probe(root) is not { } space ||
            remaining + (decimal)remaining * (decimal)options.Value.DiskHeadroomFraction > space.FreeBytes))
        {
            throw Error(ErrorCodes.InsufficientDisk, "The checkpoint staging volume does not have enough free space.");
        }

        PkiFiles.EnsureDirectory(stage);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            check();
            if (!reused[i] && manifest.Files[i].Size == 0)
            {
                using var empty = OpenStageWrite(stage, manifest.Files[i].Path);
                empty.SetLength(0);
                empty.Flush(flushToDisk: true);
            }
        }

        return journal with { Offsets = offsets, Reused = reused };
    }

    public MoveFolderManifest Manifest(string transferId, MasterTriple master, string grant)
    {
        var entry = Find(transferId);
        _ = Authorize(entry, master, grant, MoveTransferRole.Export, allowCompleted: true);
        return entry.Journal.Manifest ?? throw Error(ErrorCodes.FolderConflict, "The source manifest is still being prepared.");
    }

    public async Task ReadAsync(string transferId, MasterTriple master, string grant, int index, long start, long end,
        string etag, Func<MoveManifestFile, Stream> begin, CancellationToken ct)
    {
        var entry = Find(transferId);
        if (!await _dataSlot.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new AgentException(503, StorageErrors.Error(ErrorCodes.InternalError, "The checkpoint data channel is busy."));
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lease = Authorize(entry, master, grant, MoveTransferRole.Export);
                var journal = entry.Journal;
                var member = Member(journal, index, start, end);
                if (etag != $"\"sha256-{member.Sha256}\"")
                {
                    throw Changed();
                }

                await using var file = OpenRead(paths.Root, journal.Scope.Folder + "/" + member.Path);
                var identity = journal.Identities![index];
                void Check()
                {
                    CheckLease(entry, lease);
                    if (Identify(file) != identity)
                    {
                        throw Changed();
                    }
                }

                Check();
                file.Position = start;
                await CopyWhileFencedAsync(file, begin(member), end - start + 1, Check, lease, ct).ConfigureAwait(false);
                Check();
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            _dataSlot.Release();
        }
    }

    public async Task<MoveTransferStatus> WriteAsync(string transferId, MasterTriple master, string grant, int index,
        long start, long end, long total, string pinned, Stream body, CancellationToken ct)
    {
        var entry = Find(transferId);
        if (!await _dataSlot.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new AgentException(503, StorageErrors.Error(ErrorCodes.InternalError, "The checkpoint data channel is busy."));
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lease = Authorize(entry, master, grant, MoveTransferRole.Import);
                var journal = entry.Journal;
                var member = Member(journal, index, start, end);
                if (pinned != $"\"{journal.Manifest!.ManifestId}\"" || total != member.Size)
                {
                    throw Changed();
                }

                var status = Status(entry);
                long offset = journal.Offsets![index];
                if (end < offset)
                {
                    return status;
                }

                if (status.FileIndex != index || start != offset)
                {
                    throw Error(ErrorCodes.FolderConflict, "The requested range does not start at the durable offset.",
                        status);
                }

                string stage = Stage(entry, paths.Root);
                using var file = OpenStageWrite(stage, member.Path);
                if (file.Length < offset)
                {
                    throw Changed();
                }

                file.SetLength(offset);
                file.Position = offset;
                try
                {
                    await CopyWhileFencedAsync(body, file, end - start + 1, () => CheckLease(entry, lease), lease, ct).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    CheckLease(entry, lease);
                    var offsets = journal.Offsets.ToArray();
                    offsets[index] = end + 1;
                    Save(entry, journal with { Offsets = offsets });
                }
                catch
                {
                    file.SetLength(offset);
                    throw;
                }
                return Status(entry);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            _dataSlot.Release();
        }
    }

    public async Task<MoveTransferStatus> FinishAsync(string transferId, MasterTriple master, CancellationToken ct)
    {
        var entry = Find(transferId);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureCurrent(master);
            if (entry.Journal.Phase is MoveTransferPhase.Completed or MoveTransferPhase.Verifying)
            {
                return Status(entry);
            }

            var lease = entry.Lease;
            if (lease is null || lease.Master != master || entry.Journal.Phase != MoveTransferPhase.Ready)
            {
                throw Error(ErrorCodes.TransferLeaseRequired, "A current lease and a prepared checkpoint are required.");
            }

            CheckLease(entry, lease);
            if (Status(entry).FileIndex is not null)
            {
                throw Error(ErrorCodes.FolderConflict, "The import is still missing checkpoint bytes.");
            }

            Save(entry, entry.Journal with { Phase = MoveTransferPhase.Verifying });
            StartWorker(entry, master);
            return Status(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<MoveTransferStatus> CancelAsync(string transferId, MasterTriple master, CancellationToken ct)
    {
        var entry = Find(transferId);
        EnsureCurrent(master);
        if (entry.Lease is { } lease)
        {
            TryCancel(lease.Lifetime);
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureCurrent(master);
            if (entry.Journal.Phase is MoveTransferPhase.Preparing or MoveTransferPhase.Ready or MoveTransferPhase.Verifying)
            {
                await EndAsync(entry, MoveTransferPhase.Cancelled, null).ConfigureAwait(false);
            }

            return Status(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void VerifyExport(TransferJournal journal, Action check, CancellationToken ct)
    {
        string root = paths.Root;
        var folder = journal.Scope.Folder;
        var files = journal.Manifest!.Files;
        if (!MoveManifestRules.EnumeratePayload(Path.Combine(root, folder), ct).SequenceEqual(files.Select(f => f.Path)))
        {
            throw Changed();
        }

        for (int i = 0; i < files.Count; i++)
        {
            check();
            using var file = OpenRead(root, folder + "/" + files[i].Path);
            if (Identify(file) != journal.Identities![i])
            {
                throw Changed();
            }
        }
    }

    private async Task PublishAsync(Entry entry, Action check, CancellationToken ct)
    {
        var manifest = entry.Journal.Manifest!;
        string root = paths.Root;
        string stage = Stage(entry, root);
        var location = paths.Locate(entry.Journal.Scope.Folder);
        ValidateDestination(location, manifest, ct);
        var missing = new List<MoveManifestFile>();
        var names = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            check();
            if (await CheckExistingAsync(root, location, file, check, ct, names).ConfigureAwait(false))
            {
                continue;
            }

            await using var staged = OpenRead(root, Path.GetFileName(stage) + "/" + file.Path);
            await VerifyMemberAsync(staged, file, names, check, ct).ConfigureAwait(false);

            missing.Add(file);
        }

        check();
        if (!location.Exists)
        {
            if (!MoveManifestRules.EnumeratePayload(stage, ct).SequenceEqual(manifest.Files.Select(f => f.Path)))
            {
                throw Changed();
            }

            Directory.Move(stage, location.Path);
            DirectorySync.TryFlushParent(location.Path);
            return;
        }

        if (missing.Count > 0 && BeforeMergeForTests is { } beforeMerge)
        {
            await beforeMerge(ct).ConfigureAwait(false);
        }

        foreach (var file in missing)
        {
            check();
            ConfinedModelFile.Publish(root, Path.GetFileName(stage) + "/" + file.Path, location.Folder + "/" + file.Path);
            DirectorySync.TryFlushParent(Path.Combine(location.Path, file.Path));
        }
    }

    private static void ValidateDestination(FolderLocation location, MoveFolderManifest manifest, CancellationToken ct)
    {
        if (location.IsLink || File.Exists(location.Path))
        {
            throw Error(ErrorCodes.FolderConflict, "The destination must be a plain checkpoint folder.");
        }

        if (location.Exists)
        {
            var pathsInManifest = manifest.Files.Select(member => member.Path).ToHashSet(StringComparer.Ordinal);
            if (MoveManifestRules.EnumeratePayload(location.Path, ct).Any(path => !pathsInManifest.Contains(path)))
            {
                throw Error(ErrorCodes.FolderConflict, "The destination has unrelated checkpoint files.");
            }
        }
    }

    private static async Task<bool> CheckExistingAsync(string root, FolderLocation location, MoveManifestFile member,
        Action check, CancellationToken ct, IReadOnlySet<string>? names = null)
    {
        if (!location.Exists || !File.Exists(Path.Combine(location.Path, member.Path)))
        {
            return false;
        }

        await using var file = OpenRead(root, location.Folder + "/" + member.Path);
        var identity = Identify(file);
        if (!await MatchesMemberAsync(file, member, check, ct).ConfigureAwait(false))
        {
            throw Error(ErrorCodes.FolderConflict, "An existing destination file conflicts with the checkpoint.");
        }

        if (names is not null)
        {
            await CheckIndexAsync(member.Path, file, names, ct).ConfigureAwait(false);
        }

        if (Identify(file) != identity)
        {
            throw Changed();
        }

        return true;
    }

    private static async Task VerifyMemberAsync(FileStream stream, MoveManifestFile member, IReadOnlySet<string> names,
        Action check, CancellationToken ct)
    {
        var identity = Identify(stream);
        if (!await MatchesMemberAsync(stream, member, check, ct).ConfigureAwait(false))
        {
            throw Changed();
        }

        await CheckIndexAsync(member.Path, stream, names, ct).ConfigureAwait(false);
        if (Identify(stream) != identity)
        {
            throw Changed();
        }
    }

    private static async Task<bool> MatchesMemberAsync(FileStream stream, MoveManifestFile member, Action check, CancellationToken ct)
    {
        using var git = member.GitBlobSha1 is null ? null : GitHash(member.Size);
        return stream.Length == member.Size &&
            await HashAsync(stream, check, ct, git).ConfigureAwait(false) == member.Sha256 &&
            (git is null || string.Equals(Convert.ToHexStringLower(git.GetHashAndReset()), member.GitBlobSha1, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafetensorsIndex(string path) => path.EndsWith(".safetensors.index.json", StringComparison.OrdinalIgnoreCase);

    private static Task CheckIndexAsync(string path, Stream stream, IReadOnlySet<string> names, CancellationToken ct) =>
        IsSafetensorsIndex(path) ? MoveManifestRules.ValidateSafetensorsIndexAsync(path, stream, names, ct) : Task.CompletedTask;

    private static IncrementalHash GitHash(long size)
    {
        // Git's blob identity is compared alongside the transfer's SHA-256.
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"blob {size}\0")));
        return hash;
    }

    private static FileStream OpenStageWrite(string stage, string relative)
    {
        _ = RepoPathRules.PrepareTarget(stage, relative);
        return ConfinedModelFile.OpenWrite(stage, relative) ?? throw Changed();
    }

    private async Task EndAsync(Entry entry, MoveTransferPhase phase, AgentError? error)
    {
        Save(entry, entry.Journal with { Phase = phase, Error = error });
        if (entry.Lease is { } lease)
        {
            TryCancel(lease.Lifetime);
        }

        paths.ReleaseTransfer(entry.Journal.Scope.Folder, entry.Id, entry.Journal.Scope.Role);
        await PublishOutcomeAsync(entry).ConfigureAwait(false);
    }

    public async Task RecoverAsync(RecoverableOperation operation, CancellationToken ct)
    {
        if (operation.Status.Kind == OperationKinds.MoveTransferOpen &&
            Load(IdOf(operation.Status.Id)) is { } recovered)
        {
            var entry = _entries.GetOrAdd(recovered.Id, recovered);
            if (entry.Journal.Phase is MoveTransferPhase.Completed or MoveTransferPhase.Cancelled or MoveTransferPhase.Failed)
            {
                await PublishOutcomeAsync(entry).ConfigureAwait(false);
            }
            return;
        }

        await operations.CompleteAsync(operation.Status.Id, OperationState.Failed,
            StorageErrors.Error(ErrorCodes.InvalidState, "The interrupted transfer action needs a new request."),
            ct: ct).ConfigureAwait(false);
    }

    private async Task PublishOutcomeAsync(Entry entry)
    {
        var journal = entry.Journal;
        if (operations.Log.Find(journal.Scope.OperationId) is { State: var state } && !state.IsTerminal())
        {
            await operations.CompleteAsync(journal.Scope.OperationId, journal.Phase switch
            {
                MoveTransferPhase.Completed => OperationState.Succeeded,
                MoveTransferPhase.Cancelled => OperationState.Cancelled,
                _ => OperationState.Failed,
            }, journal.Error, AgentJson.ToElement(Status(entry)), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string PathFor(string id) => Path.Combine(_journalRoot, id + ".json");
    private static string Stage(Entry entry, string root) => Path.Combine(root, ".move-" + entry.Id);

    private void Save(Entry entry, TransferJournal journal)
    {
        PkiFiles.WriteSecret(PathFor(entry.Id), JsonSerializer.Serialize(journal, AgentJson.Options));
        entry.Journal = journal;
    }

    private Entry Find(string id)
    {
        if (!ContentDigest.IsHex(id, 32))
        {
            throw StorageErrors.NotFound("No such checkpoint transfer.");
        }

        return _entries.GetOrAdd(id, key => Load(key) ?? throw StorageErrors.NotFound("No such checkpoint transfer."));
    }

    private Entry? Load(string id) => PkiFiles.ReadIfExists(PathFor(id)) is { } json &&
        JsonSerializer.Deserialize<TransferJournal>(json, AgentJson.Options) is { } state ? new Entry(id, state) : null;

    private static MoveTransferStatus Status(Entry entry)
    {
        var state = entry.Journal;
        int? index = null;
        long? next = null;
        long bytes = state.Phase == MoveTransferPhase.Completed ? state.TotalBytes : 0;
        if (state.Manifest is { } manifest && state.Offsets is { } offsets && state.Phase != MoveTransferPhase.Completed)
        {
            bytes = offsets.Sum();
            for (int i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] < manifest.Files[i].Size)
                {
                    index = i;
                    next = offsets[i];
                    break;
                }
            }
        }

        return new MoveTransferStatus(entry.Id, state.Scope.OperationId, state.Scope.Role, state.Phase, bytes,
            state.TotalBytes, index, next, state.Error);
    }

    private LeaseState Authorize(Entry entry, MasterTriple master, string grant, MoveTransferRole role, bool allowCompleted = false)
    {
        var lease = Volatile.Read(ref entry.Lease);
        var journal = entry.Journal;
        if (lease is null || lease.Master != master || journal.Scope.Role != role ||
            !ContentDigest.IsHex(grant, 64) ||
            !CryptographicOperations.FixedTimeEquals(lease.Hash, SHA256.HashData(Convert.FromHexString(grant))) ||
            journal.Phase != MoveTransferPhase.Ready && !(allowCompleted && journal.Phase == MoveTransferPhase.Completed))
        {
            throw Error(ErrorCodes.TransferLeaseRequired, "A valid lease for this checkpoint transfer is required.");
        }

        CheckLease(entry, lease);
        return lease;
    }

    private void CheckLease(Entry entry, LeaseState lease)
    {
        if (entry.Lease != lease || lease.Expires <= time.GetUtcNow() || !operations.Fencing.IsCurrent(lease.Master) ||
            lease.Lifetime.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            throw Error(ErrorCodes.TransferLeaseRequired, "The checkpoint transfer lease has ended.");
        }
    }

    private void EnsureCurrent(MasterTriple master)
    {
        if (!operations.Fencing.IsCurrent(master))
        {
            throw Error(ErrorCodes.StaleMaster, "The master no longer owns the checkpoint transfer.");
        }
    }

    private static MoveManifestFile Member(TransferJournal state, int index, long start, long end)
    {
        if (state.Manifest is not { } manifest || index < 0 || index >= manifest.Files.Count || start < 0 ||
            end < start || end >= manifest.Files[index].Size || end - start >= Protocol.MoveRangeBytes)
        {
            throw Error(ErrorCodes.InvalidRange, "The file index or byte range lies outside the checkpoint manifest.");
        }

        return manifest.Files[index];
    }

    private static FileStream OpenRead(string root, string name) => ConfinedModelFile.Open(root, name) ?? throw Changed();
    private static ConfinedModelFile.Identity Identify(FileStream file) => ConfinedModelFile.Identify(file.SafeFileHandle) ?? throw Changed();
    private static AgentException Changed() => Error(ErrorCodes.TransferChanged, "The checkpoint changed or no longer matches its manifest.");

    private static AgentException Error(string code, string message, object? details = null) =>
        StorageErrors.Create(code, message, details);

    private async Task CopyWhileFencedAsync(Stream source, Stream target, long count, Action check, LeaseState lease,
        CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Lifetime.Token, _shutdown.Token);
        var watch = WatchFenceAsync(lease.Master, lifetime);
        try
        {
            await CopyAsync(source, target, count, check, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }
    }

    private async Task WatchFenceAsync(MasterTriple master, CancellationTokenSource lifetime)
    {
        try
        {
            while (operations.Fencing.IsCurrent(master))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), time, lifetime.Token).ConfigureAwait(false);
            }
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private static async Task CopyAsync(Stream source, Stream target, long count, Action check, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            while (count > 0)
            {
                check();
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The checkpoint stream ended before the declared range.");
                }

                check();
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> HashAsync(Stream file, Action check, CancellationToken ct, IncrementalHash? git = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            int read;
            while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                check();
                hash.AppendData(buffer, 0, read);
                git?.AppendData(buffer, 0, read);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryCancel(CancellationTokenSource lifetime)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Expiry already removed this lease.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var entry in _entries.Values)
        {
            if (entry.Lease is { } lease)
            {
                TryCancel(lease.Lifetime);
            }
            await entry.Preparation.ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _dataSlot.Dispose();
    }

    private sealed class Entry(string id, TransferJournal journal)
    {
        public string Id { get; } = id;
        public volatile TransferJournal Journal = journal;
        public LeaseState? Lease;
        public SemaphoreSlim Gate { get; } = new(1);
        public Task Preparation { get; set; } = Task.CompletedTask;
    }

    private sealed record LeaseState(byte[] Hash, MasterTriple Master, DateTimeOffset Expires, CancellationTokenSource Lifetime);
    private sealed record TransferJournal(MoveTransferOpenRequest Scope, MoveTransferPhase Phase, MoveFolderManifest? Manifest,
        long TotalBytes, List<ConfinedModelFile.Identity>? Identities = null,
        long[]? Offsets = null, IReadOnlyList<bool>? Reused = null, AgentError? Error = null);
}
