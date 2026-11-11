using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Stops a Ready model that has had no request for its <see cref="ModelDefinition.IdleStopMinutes"/>. The idle time counts
/// from the later of the model's last request and the moment this service first saw it Ready, so a model that just
/// started gets its full limit, and a master restart starts the count again (the clock is in memory).
/// <para>Never stops a model with a request in flight or waiting (<see cref="IRouterStats.InUse"/>), one that is not Ready,
/// or one a benchmark, tuning run, move or restore holds (<see cref="AutoStopGuard"/>). The stop is an ordinary
/// <see cref="IModelRuntime.StopAsync"/> under the <see cref="AuditActors.SystemIdleStop"/> actor, so recovery treats it
/// like a stop by hand: the model is not brought back after a node or master restart.</para>
/// </summary>
/// <param name="stats">The router's view of use; null (no router composed) = nothing is ever stopped.</param>
public sealed partial class IdleStopService(
    IModelRegistry registry,
    IModelRuntime runtime,
    IRouterStats? stats,
    ILogger<IdleStopService> logger,
    TimeProvider? timeProvider = null,
    IMaintenanceMode? maintenance = null,
    IOwnershipStore? ownership = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _readySince = new(StringComparer.Ordinal);

    /// <summary>How often idle models are looked for. The limit is in minutes, so seconds of slack do not matter.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (stats is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One pass over every model. A failure costs one log line, never the host.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        if (stats is null)
        {
            return;
        }

        try
        {
            var models = await registry.ListAsync(ct).ConfigureAwait(false);
            var ready = (await runtime.ListStatusAsync(ct).ConfigureAwait(false))
                .Where(s => s.State == ModelState.Ready).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            foreach (string gone in _readySince.Keys.Where(id => !ready.Contains(id)).ToList())
            {
                _readySince.Remove(gone);
            }

            foreach (var model in models.Where(m => ready.Contains(m.Id)))
            {
                // Every Ready model is tracked, so a limit set on a running model counts from when it became Ready.
                if (!_readySince.TryGetValue(model.Id, out var readySince))
                {
                    _readySince[model.Id] = readySince = now;
                }

                if (model.IdleStopMinutes is not { } minutes)
                {
                    continue;
                }

                var idleSince = stats.LastRequestAt(model.ServedName) is { } last && last > readySince ? last : readySince;
                if (now - idleSince < TimeSpan.FromMinutes(minutes) || stats.InUse(model.ServedName) ||
                    await AutoStopGuard.HoldsAsync(model, maintenance, ownership, ct).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    using var audit = AuditScope.Begin(AuditActors.SystemIdleStop);
                    await runtime.StopAsync(model.Id, ct).ConfigureAwait(false);
                    IdleStopped(logger, model.Id, minutes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    IdleStopFailed(logger, ex, model.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SweepFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped model {ModelId}: no request for {Minutes} minutes (its idle limit).")]
    private static partial void IdleStopped(ILogger logger, string modelId, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not stop idle model {ModelId}; it is tried again on the next pass.")]
    private static partial void IdleStopFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The idle-stop pass failed; it runs again on the next tick.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}

/// <summary>What an automatic stop (idle stop, make room) must leave alone besides a model in use.</summary>
internal static class AutoStopGuard
{
    /// <summary>
    /// True when a benchmark or tuning window is open on the model's node, measures the model itself or is global, or when
    /// a move, job or restore holds the model (an ownership record).
    /// </summary>
    public static async Task<bool> HoldsAsync(ModelDefinition model, IMaintenanceMode? maintenance, IOwnershipStore? ownership, CancellationToken ct)
    {
        string? nodeId = model.Placement?.NodeId;
        if (maintenance?.Active.Any(w => w.IsGlobal || w.ModelId == model.Id || (nodeId is not null && w.NodeId == nodeId)) == true)
        {
            return true;
        }

        return ownership is not null && await ownership.GetForModelAsync(model.Id, ct).ConfigureAwait(false) is not null;
    }
}
