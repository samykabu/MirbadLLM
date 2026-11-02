namespace MirbadLLM.Agent.Storage;

/// <summary>Free and total bytes of the volume holding a path.</summary>
/// <param name="FreeBytes">Bytes available to the agent.</param>
/// <param name="TotalBytes">Volume size.</param>
public sealed record DiskSpace(long FreeBytes, long TotalBytes);

/// <summary>Reads free space of a volume (download pre-check §6.4, model-root health §3.5, capability report §3.3).</summary>
public interface IDiskSpaceProbe
{
    /// <summary>Space of the volume holding <paramref name="path"/>; null when it cannot be read.</summary>
    DiskSpace? Probe(string path);
}

/// <summary><see cref="IDiskSpaceProbe"/> over <see cref="DriveInfo"/> (<c>statvfs</c> on Linux).</summary>
public sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    /// <inheritdoc />
    public DiskSpace? Probe(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(path))! : path);
            return new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
