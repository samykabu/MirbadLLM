using System.Security.Cryptography;
using System.Text;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// The one argv digest of process delivery (MN-207, D121): 16 lowercase hex characters of SHA-256 over the substituted
/// arguments (what actually ran; <c>argv[0]</c> excluded), the elements joined with U+0000. Used for
/// <c>ProcessRecord.ArgvDigest</c>, <c>PendingProcessStart.ArgvDigest</c> and the orphan sweep's comparison with
/// <c>IProcessHost.ReadArgumentsAsync</c>, so the three always agree.
/// </summary>
public static class ProcessArgvDigest
{
    /// <summary>Length of the digest in hex characters.</summary>
    public const int Length = 16;

    /// <summary>The digest of <paramref name="arguments"/> (no <c>argv[0]</c>).</summary>
    public static string Compute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(a => a is null))
        {
            throw new ArgumentException("An argument is null.", nameof(arguments));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', arguments)));
        return Convert.ToHexStringLower(hash)[..Length];
    }
}
