using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>
/// Writes the identity an enrollment produced into the state directory (<see cref="FileAgentCredentials"/>' files) so a
/// crash at any point leaves either the complete new identity or one that does not load (not enrolled), never an old
/// certificate with a new uid:
/// <list type="number">
/// <item>the previous identity's rotation state (<c>pki-rotation.json</c>, <c>agent.pending.*</c>) is removed;</item>
/// <item><c>agent.key</c> (0600): it no longer matches any previous <c>agent.crt</c>, so the old identity stops
/// loading at once;</item>
/// <item><c>ca.crt</c>, then <c>node-uid</c>;</item>
/// <item><c>agent.crt</c> last: the commit point. Without it (or with the old one) the pair does not match.</item>
/// </list>
/// Each file is written atomically (temp + flush + rename, <see cref="PkiFiles"/>) and the directory is fsynced after
/// each rename on Linux, so the order survives a power loss. <see cref="FileAgentCredentials"/> treats a certificate
/// without <c>node-uid</c> as not enrolled (W2 review MN-092).
/// </summary>
internal static class EnrollmentStateFiles
{
    /// <summary>True when the directory holds a certificate or a node uid (an identity, or part of one, that <c>join</c> would replace).</summary>
    public static bool HasIdentity(string directory) =>
        File.Exists(Path.Combine(directory, FileAgentCredentials.CertificateFileName))
        || !string.IsNullOrWhiteSpace(PkiFiles.ReadIfExists(Path.Combine(directory, FileAgentCredentials.NodeUidFileName)));

    /// <summary>Writes the new identity in the order above.</summary>
    public static void Write(string directory, string keyPem, string caPem, string nodeUid, string certificatePem)
    {
        PkiFiles.EnsureDirectory(directory);

        // A rotation of the previous identity must not survive a re-enrollment (join --force, bootstrap over a lost
        // identity): at the next start RotatingAgentCredentials would present its pending certificate, or promote its
        // pending key over the new one (W3 review MN-094). Removed first, so no crash point leaves it next to the new key.
        bool removed = false;
        foreach (string stale in new[] { AgentRotationStore.DocumentFileName, AgentRotationStore.PendingCertificateFileName, AgentRotationStore.PendingKeyFileName })
        {
            string path = Path.Combine(directory, stale);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }
        }

        if (removed)
        {
            DirectorySync.Flush(directory);
        }

        Commit(directory, FileAgentCredentials.KeyFileName, keyPem, secret: true);
        Commit(directory, FileAgentCredentials.CaFileName, caPem, secret: false);
        Commit(directory, FileAgentCredentials.NodeUidFileName, nodeUid + "\n", secret: false);
        Commit(directory, FileAgentCredentials.CertificateFileName, certificatePem, secret: false);
    }

    private static void Commit(string directory, string fileName, string content, bool secret)
    {
        string path = Path.Combine(directory, fileName);
        if (secret)
        {
            PkiFiles.WriteSecret(path, content);
        }
        else
        {
            PkiFiles.WritePublic(path, content);
        }

        DirectorySync.Flush(directory);
    }
}
