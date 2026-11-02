using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Removes the CNG key files an unclean exit left in the service account's profile (design-p3p4 §A.4.7, D122; spike S4
/// F3). <c>TlsCredentials</c> loads with <c>DefaultKeySet</c>, which persists every live certificate's private key under
/// <c>%APPDATA%\Microsoft\Crypto\Keys</c> (RSA: <c>Crypto\RSA\&lt;SID&gt;</c>) until the certificate is disposed; a crash,
/// kill, power loss or failed update leaves one file per live certificate there for good. The sweep runs first in
/// <c>Program.CreateAsync</c>, before any credential is loaded, and deletes every file in those two folders, but
/// <b>only</b> when the process token's user is the dedicated virtual account <c>NT SERVICE\mirbadllm-agent</c>: a
/// console or dev run under a real user never touches that user's keys. Safe because the account belongs to the agent
/// alone and the llama-server children never create keys. Machine keys (<c>%ProgramData%\Microsoft\Crypto\Keys</c>) are
/// never touched. Failures are logged, never fatal.
/// </summary>
public sealed partial class ServiceProfileKeySweep
{
    private readonly string? _currentUserSid;
    private readonly string? _appDataRoot;
    private readonly ILogger _logger;

    /// <summary>
    /// A sweep for an explicit identity and profile (tests inject both; <see cref="ForCurrentProcess"/> reads them from the
    /// process). <paramref name="currentUserSid"/> is the token user's SID string (null = unknown: no sweep);
    /// <paramref name="appDataRoot"/> is that user's roaming application data folder (<c>%APPDATA%</c>).
    /// </summary>
    public ServiceProfileKeySweep(string? currentUserSid, string? appDataRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _currentUserSid = currentUserSid;
        _appDataRoot = appDataRoot;
        _logger = logger;
    }

    /// <summary>True when the identity is <c>NT SERVICE\mirbadllm-agent</c>, i.e. <see cref="Run"/> would sweep.</summary>
    public bool IsServiceAccount => string.Equals(_currentUserSid, AgentServiceSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>The SID of this install's service account (<see cref="ServiceSidFor"/> of <see cref="WindowsServiceHosting.ServiceName"/>).</summary>
    public static string AgentServiceSid { get; } = ServiceSidFor(WindowsServiceHosting.ServiceName);

    /// <summary>
    /// The sweep for this process: on Windows the token user (<see cref="WindowsIdentity.GetCurrent()"/>) and its
    /// <c>%APPDATA%</c>; elsewhere an identity-less sweep that does nothing.
    /// </summary>
    public static ServiceProfileKeySweep ForCurrentProcess(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ServiceProfileKeySweep(null, null, logger);
        }

        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        return new ServiceProfileKeySweep(sid, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), logger);
    }

    /// <summary>
    /// The service SID Windows derives from a service name (<c>NT SERVICE\&lt;name&gt;</c>): <c>S-1-5-80-</c> followed by the
    /// five little-endian 32-bit words of SHA-1 over the upper-cased name in UTF-16LE. For <c>mirbadllm-agent</c> this is
    /// <c>S-1-5-80-383857687-2312895033-837081144-4191421113-4061622124</c> (S4 logged the same value under the SCM).
    /// </summary>
    public static string ServiceSidFor(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
#pragma warning disable CA5350 // SHA-1 is what Windows uses to derive service SIDs; this is not a security use.
        byte[] hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
#pragma warning restore CA5350
        var sid = new StringBuilder("S-1-5-80", 64);
        for (int i = 0; i < 5; i++)
        {
            sid.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)).ToString(CultureInfo.InvariantCulture));
        }

        return sid.ToString();
    }

    /// <summary>The folders the sweep empties for <paramref name="sid"/> under <paramref name="appDataRoot"/>.</summary>
    public static IReadOnlyList<string> KeyFolders(string appDataRoot, string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        string crypto = Path.Combine(appDataRoot, "Microsoft", "Crypto");
        return [Path.Combine(crypto, "Keys"), Path.Combine(crypto, "RSA", sid)];
    }

    /// <summary>
    /// Deletes the leaked key files when the identity is the agent's service account; otherwise does nothing. Never
    /// throws for file-system failures (each one is logged and counted).
    /// </summary>
    public KeySweepResult Run()
    {
        if (!IsServiceAccount)
        {
            LogNotServiceAccount(_logger, _currentUserSid ?? "(unknown)", AgentServiceSid);
            return KeySweepResult.NotServiceAccount;
        }

        if (string.IsNullOrWhiteSpace(_appDataRoot))
        {
            LogNoProfile(_logger);
            return KeySweepResult.NotServiceAccount;
        }

        int deleted = 0;
        int failed = 0;
        foreach (string folder in KeyFolders(_appDataRoot, AgentServiceSid))
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists)
                {
                    continue;
                }

                // A redirected folder (junction or link) is not the profile's own key store: leave it alone.
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    LogFolderRedirected(_logger, folder);
                    continue;
                }

                foreach (FileInfo file in info.EnumerateFiles())
                {
                    try
                    {
                        if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        file.Delete();
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        LogFileNotDeleted(_logger, ex, file.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                LogFolderNotSwept(_logger, ex, folder);
            }
        }

        LogSwept(_logger, deleted, failed);
        return new KeySweepResult(true, deleted, failed);
    }

    [LoggerMessage(EventId = 2660, Level = LogLevel.Debug,
        Message = "Key sweep skipped: the agent runs as {Sid}, not as the service account {ServiceSid}")]
    private static partial void LogNotServiceAccount(ILogger logger, string sid, string serviceSid);

    [LoggerMessage(EventId = 2661, Level = LogLevel.Warning, Message = "Key sweep skipped: the service account has no application data folder")]
    private static partial void LogNoProfile(ILogger logger);

    [LoggerMessage(EventId = 2662, Level = LogLevel.Information,
        Message = "Key sweep: deleted {Deleted} key file(s) left by an unclean exit from the service profile ({Failed} could not be deleted)")]
    private static partial void LogSwept(ILogger logger, int deleted, int failed);

    [LoggerMessage(EventId = 2663, Level = LogLevel.Warning, Message = "Key sweep could not delete {Path}")]
    private static partial void LogFileNotDeleted(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 2664, Level = LogLevel.Warning, Message = "Key sweep could not list {Folder}")]
    private static partial void LogFolderNotSwept(ILogger logger, Exception exception, string folder);

    [LoggerMessage(EventId = 2665, Level = LogLevel.Warning, Message = "Key sweep left {Folder} alone: it is a junction or link")]
    private static partial void LogFolderRedirected(ILogger logger, string folder);
}

/// <summary>What one <see cref="ServiceProfileKeySweep.Run"/> did.</summary>
/// <param name="Ran">True when the identity was the agent's service account and the folders were swept.</param>
/// <param name="Deleted">Key files deleted.</param>
/// <param name="Failed">Files or folders that could not be deleted or listed.</param>
public readonly record struct KeySweepResult(bool Ran, int Deleted, int Failed)
{
    /// <summary>The process does not run as <c>NT SERVICE\mirbadllm-agent</c>: nothing was touched.</summary>
    public static KeySweepResult NotServiceAccount => default;
}
