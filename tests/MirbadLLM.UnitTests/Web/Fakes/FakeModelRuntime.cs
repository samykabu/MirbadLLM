using System.Runtime.CompilerServices;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>
/// Hand-written fake <see cref="IModelRuntime"/>. Throws <see cref="ModelMemoryAdmissionException"/> when
/// <see cref="RefuseUnlessForce"/> is set and start is called without <c>force</c>.
/// </summary>
public sealed class FakeModelRuntime : IModelRuntime
{
    private readonly Dictionary<string, ModelStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _logs = new(StringComparer.Ordinal);

    public bool RefuseUnlessForce { get; set; }

    public string RefusalMessage { get; set; } = "Model needs 64 GiB but only 40 GiB is available.";

    /// <summary>The refusal <see cref="RefuseUnlessForce"/> throws (e.g. one carrying the agent's numbers); null = <see cref="RefusalMessage"/>.</summary>
    public ModelMemoryAdmissionException? Refusal { get; set; }

    public List<(string Id, bool Force)> StartCalls { get; } = [];

    /// <summary>When set, start, restart and stop throw it (the multi-node runtime's node refusals, MN-094).</summary>
    public Exception? Failure { get; set; }

    public List<string> StopCalls { get; } = [];

    public void SetStatus(ModelStatus status) => _statuses[status.Id] = status;

    public void SetLogs(string id, params string[] lines) => _logs[id] = lines;

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelStatus>>(_statuses.Values.ToList());

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_statuses.GetValueOrDefault(id) ?? new ModelStatus(id, ModelState.Stopped));

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        StartCalls.Add((id, force));
        if (Failure is { } failure)
        {
            throw failure;
        }

        if (RefuseUnlessForce && !force)
        {
            throw Refusal ?? new ModelMemoryAdmissionException(RefusalMessage);
        }

        var status = new ModelStatus(id, ModelState.Starting, Since: DateTimeOffset.UtcNow);
        _statuses[id] = status;
        return Task.FromResult(status);
    }

    public Task StopAsync(string id, CancellationToken ct = default)
    {
        StopCalls.Add(id);
        if (Failure is { } failure)
        {
            throw failure;
        }

        _statuses[id] = new ModelStatus(id, ModelState.Stopped);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var line in _logs.GetValueOrDefault(id, []))
        {
            ct.ThrowIfCancellationRequested();
            yield return line;
            await Task.Yield();
        }
    }
}
