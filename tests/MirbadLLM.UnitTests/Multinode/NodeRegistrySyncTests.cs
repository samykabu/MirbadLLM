using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.Fakes;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// Shutdown of <see cref="NodeRegistrySync"/> (MN-044 note, fixed by MN-047): a host whose provider was disposed before
/// it was stopped (a crashed test master, a failed start under load) stopped with <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class NodeRegistrySyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StopAfterDisposeAndASecondStopAreQuiet()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        string dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-registry-sync-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
            using var master = agent.Ca.IssueMasterClientCertificate();
            using var trust = MirbadLLM.Pki.TrustAnchorSet.Create([agent.Ca.Certificate]);
            await using var factory = new AgentClientFactory(registry, new TestMasterIdentity(), master, trust, TimeProvider.System);

            var disposedFirst = new NodeRegistrySync(registry, factory, TimeProvider.System, interval: TimeSpan.FromMilliseconds(20));
            await disposedFirst.StartAsync(Ct);
            disposedFirst.Dispose();
            await disposedFirst.StopAsync(Ct);
            await disposedFirst.StopAsync(Ct);
            disposedFirst.Dispose();

            using var stoppedTwice = new NodeRegistrySync(registry, factory, TimeProvider.System, interval: TimeSpan.FromMilliseconds(20));
            await stoppedTwice.StartAsync(Ct);
            await Task.Delay(60, Ct);
            await stoppedTwice.StopAsync(Ct);
            await stoppedTwice.StopAsync(Ct);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
