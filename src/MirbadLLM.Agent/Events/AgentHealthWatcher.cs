using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// Publishes the node's health as a <see cref="HealthEvent"/> whenever it changes (design §3.4/§3.5, MN-047 / D68): every
/// <paramref name="interval"/> it asks the health contributors (the same union <c>/status</c> reports) and publishes when
/// the set of reason codes differs from the last published one. A boot starts from "healthy", so an agent that starts
/// with Docker down publishes its first health event at the first check. Comparing codes only keeps a changing error
/// text (e.g. Docker's message) from producing an event per check. Contributors are fast and never throw; a check that
/// throws anyway is logged and retried at the next interval.
/// </summary>
internal sealed partial class AgentHealthWatcher(
    IEventBus bus,
    IEnumerable<IHealthContributor> contributors,
    TimeProvider time,
    ILogger<AgentHealthWatcher> logger,
    TimeSpan interval) : BackgroundService
{
    private string[] _published = [];

    /// <summary>Checks once and publishes when the reason codes changed. Returns true when it published.</summary>
    internal async Task<bool> CheckAsync(CancellationToken ct)
    {
        NodeHealth health = await StatusEndpoints.CheckHealthAsync(contributors, ct).ConfigureAwait(false);
        string[] codes = [.. health.Reasons.Select(r => r.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (codes.SequenceEqual(_published, StringComparer.Ordinal))
        {
            return false;
        }

        bus.Publish(new HealthEvent { Health = health });
        LogHealthChanged(logger, _published.Length == 0 ? "healthy" : string.Join(", ", _published), codes.Length == 0 ? "healthy" : string.Join(", ", codes));
        _published = codes;
        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCheckFailed(logger, ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [LoggerMessage(EventId = 2701, Level = LogLevel.Information, Message = "Node health changed: {Previous} → {Current}")]
    private static partial void LogHealthChanged(ILogger logger, string previous, string current);

    [LoggerMessage(EventId = 2702, Level = LogLevel.Warning, Message = "Health check failed: {Error}")]
    private static partial void LogCheckFailed(ILogger logger, string error);
}
