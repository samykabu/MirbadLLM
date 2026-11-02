using System.Reflection;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// The fault-injecting layer on the master's agent clients (design §13.7): the real <c>AgentClientFactory</c> stays
/// registered and builds the real clients; this decorator, registered as <see cref="IAgentClientFactory"/>, hands every
/// caller (operation runner, status cache, runtime, router) a client whose mutations can lose their response <b>after</b>
/// the agent answered it, which a TCP fault cannot target. The caller then sees what a cut response looks like
/// (<see cref="AgentUnreachableException"/>, not retried because a response had started).
/// </summary>
internal sealed class ChaosAgentClientFactory(IAgentClientFactory inner, ChaosFaults faults) : IAgentClientFactory
{
    public IAgentClient GetClient(string nodeId) => DroppingAgentClient.Wrap(inner.GetClient(nodeId), faults);

    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => inner.CreateFreshClientAsync(nodeId, ct);

    public void RebuildHandlers(string nodeId) => inner.RebuildHandlers(nodeId);

    public void SetNegotiatedProtocol(string nodeId, int version) => inner.SetNegotiatedProtocol(nodeId, version);
}

/// <summary>Fault switches shared by the decorator and the sequence runner.</summary>
internal sealed class ChaosFaults
{
    private int _dropMutationResponses;
    private int _dropped;

    /// <summary>The next <paramref name="count"/> mutation responses are dropped after the agent answered them.</summary>
    public void DropNextMutationResponses(int count) => Interlocked.Exchange(ref _dropMutationResponses, count);

    public void ClearDrops() => Interlocked.Exchange(ref _dropMutationResponses, 0);

    /// <summary>Responses actually dropped so far.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    public bool TakeDrop()
    {
        while (true)
        {
            int left = Volatile.Read(ref _dropMutationResponses);
            if (left <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _dropMutationResponses, left - 1, left) == left)
            {
                Interlocked.Increment(ref _dropped);
                return true;
            }
        }
    }
}

/// <summary><see cref="DispatchProxy"/> over <see cref="IAgentClient"/>: forwards everything, drops armed mutation responses.</summary>
public class DroppingAgentClient : DispatchProxy
{
    private static readonly MethodInfo DropAfterMethod = typeof(DroppingAgentClient).GetMethod(nameof(DropAfterAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private IAgentClient _inner = null!;
    private ChaosFaults _faults = null!;

    internal static IAgentClient Wrap(IAgentClient inner, ChaosFaults faults)
    {
        var proxy = Create<IAgentClient, DroppingAgentClient>();
        var self = (DroppingAgentClient)(object)proxy;
        self._inner = inner;
        self._faults = faults;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        bool mutation = args is not null && args.Any(a => a is AgentMutation);
        Type returns = targetMethod.ReturnType;
        if (!mutation || !returns.IsGenericType || returns.GetGenericTypeDefinition() != typeof(Task<>) || !_faults.TakeDrop())
        {
            return result;
        }

        return DropAfterMethod.MakeGenericMethod(returns.GetGenericArguments()[0]).Invoke(null, [result, _inner.NodeId, targetMethod.Name]);
    }

    private static async Task<T> DropAfterAsync<T>(Task<T> sent, string nodeId, string method)
    {
        // The agent answers (or fails) first: only an answered request has a response to lose.
        _ = await sent.ConfigureAwait(false);
        throw new AgentUnreachableException(nodeId, $"chaos: the response of {method} was dropped after the agent answered it", null);
    }
}
