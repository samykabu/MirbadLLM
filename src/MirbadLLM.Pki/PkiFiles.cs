using System.Text;

namespace MirbadLLM.Pki;

/// <summary>
/// Atomic PEM file writes for PKI material (design §10.2: <c>ca.key</c> 0600; agents keep <c>agent.key</c> 0600). A file
/// is written to a temporary sibling created with its final permissions (owner read/write only for secrets on Unix),
/// flushed to disk and moved over the target, so a crash leaves either the old or the new file, never a partial one, and
/// a secret is never readable by others, even briefly. On Windows the directory ACL governs access.
/// </summary>
public static class PkiFiles
{
    private const UnixFileMode SecretMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PublicMode = SecretMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Writes secret material (private keys) with mode 0600.</summary>
    public static void WriteSecret(string path, string content) => Write(path, content, SecretMode);

    /// <summary>Writes public material (certificates) with mode 0644.</summary>
    public static void WritePublic(string path, string content) => Write(path, content, PublicMode);

    /// <summary>Reads a file, or null when it does not exist.</summary>
    public static string? ReadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.ASCII) : null;
    }

    /// <summary>Creates a directory for PKI material (mode 0700 on Unix) if it does not exist.</summary>
    public static void EnsureDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, DirectoryMode);
        }
    }

    private static void Write(string path, string content, UnixFileMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        EnsureDirectory(directory);

        string temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = mode;
        }

        try
        {
            using (FileStream stream = new(temp, options))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, fullPath, overwrite: true);
            MirbadLLM.Core.DirectorySync.TryFlushParent(fullPath); // best effort (D69)
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort; the temp name is unique and dot-prefixed.
            }

            throw;
        }
    }
}
