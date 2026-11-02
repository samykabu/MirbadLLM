using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// <see cref="DirectorySync"/> (D45, D69): a real directory flushes (fsync on Linux, no-op elsewhere); the best-effort form
/// the master's stores use reports a directory it cannot flush instead of throwing.
/// </summary>
public sealed class DirectorySyncTests
{
    [Fact]
    public void AnExistingDirectoryFlushesAndAMissingOneIsReportedNotThrownByTheBestEffortForm()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-dirsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "state.json");
            File.WriteAllText(file, "{}");
            DirectorySync.FlushParent(file);
            Assert.True(DirectorySync.TryFlushParent(file));

            string missing = Path.Combine(dir, "gone", "state.json");
            Assert.Equal(!OperatingSystem.IsLinux(), DirectorySync.TryFlushParent(missing));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
