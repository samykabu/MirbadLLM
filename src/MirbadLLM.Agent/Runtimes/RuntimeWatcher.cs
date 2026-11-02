using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// Watches the runtime instances (design §4.7, §4.8): every <see cref="AgentRuntimeOptions.WatchInterval"/> it lists the
/// drivers, publishes runtime state changes through the <see cref="RuntimeStateBoard"/> (in order, each with its seq),
/// and settles launches:
/// <list type="bullet">
/// <item>a launch waiting for its instance ends Succeeded when it is Ready, Failed when it failed or vanished (unless a stop
/// of that model is running, which ends it Cancelled itself); its reservation is released either way;</item>
/// <item>an active launch at stage <c>started</c> that nothing in this process tracks (agent restart) is adopted;</item>
/// <item>a reservation whose launch is not executing here is released when its instance is Ready or Failed, or when its
/// operation is terminal or unknown (reservations rebuilt after a restart, §4.8).</item>
/// </list>
/// A failed listing changes nothing (no removals are inferred from an error).
/// </summary>
public sealed class RuntimeWatcher : BackgroundService
{
    private readonly RuntimeOperations _operations;
    private readonly RuntimeStateBoard _board;
    private readonly IReservationManager _reservations;
    private readonly TimeProvider _time;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    /// <summary>Creates the watcher.</summary>
    public RuntimeWatcher(
        RuntimeOperations operations,
        RuntimeStateBoard board,
        IReservationManager reservations,
        TimeProvider? time = null,
        AgentRuntimeOptions? options = null,
        ILogger<RuntimeWatcher>? logger = null)
    {
        _operations = operations;
        _board = board;
        _reservations = reservations;
        _time = time ?? TimeProvider.System;
        _options = options ?? new AgentRuntimeOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _pollLock.Dispose();
        base.Dispose();
    }

    /// <summary>One watch round: list, publish changes, settle launches and reservations. Safe to call concurrently.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        await _pollLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long version = _board.BeginObservation();

            // Only launches already waiting when the listing starts are judged by it: a launch that reports its instance
            // while the drivers are being listed is missing from this listing, which is not "it disappeared" (MN-047).
            var awaitingBeforeListing = _operations.AwaitingReady.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<RuntimeStatus> listing;
            try
            {
                listing = await _operations.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RuntimeLog.ListFailed(_logger, ex.Message);
                return;
            }

            _board.Apply(listing, version);

            var byModel = new Dictionary<string, RuntimeStatus>(StringComparer.Ordinal);
            foreach (var instance in listing)
            {
                byModel.TryAdd(instance.ModelId, instance);
            }

            AdoptStartedLaunches();
            await SettleAwaitingAsync(byModel, awaitingBeforeListing).ConfigureAwait(false);
            await SettleReservationsAsync(byModel).ConfigureAwait(false);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_options.WatchInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void AdoptStartedLaunches()
    {
        foreach (var operation in _operations.Log.ListActive())
        {
            if (string.Equals(operation.Kind, OperationKinds.Launch, StringComparison.Ordinal)
                && string.Equals(operation.Stage, OperationStages.Started, StringComparison.Ordinal))
            {
                _operations.AdoptAwaiting(operation.Id, operation.Target);
            }
        }
    }

    private async Task SettleAwaitingAsync(Dictionary<string, RuntimeStatus> byModel, HashSet<string> awaitingBeforeListing)
    {
        foreach (var (operationId, modelId) in _operations.AwaitingReady)
        {
            if (!awaitingBeforeListing.Contains(operationId))
            {
                continue; // started waiting after the listing was taken: the next round judges it
            }

            if (!byModel.TryGetValue(modelId, out var instance))
            {
                if (!_operations.IsStopping(modelId))
                {
                    await _operations.FinishLaunchAsync(operationId, OperationState.Failed, new AgentError
                    {
                        Code = ErrorCodes.InternalError,
                        Message = "The instance of " + modelId + " disappeared before it was Ready.",
                    }, null).ConfigureAwait(false);
                }

                continue;
            }

            await SettleAsync(operationId, instance).ConfigureAwait(false);
        }
    }

    private async Task SettleReservationsAsync(Dictionary<string, RuntimeStatus> byModel)
    {
        var awaiting = _operations.AwaitingReady.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var reservation in _reservations.List())
        {
            string operationId = reservation.OperationId;
            if (_operations.IsExecuting(operationId) || awaiting.Contains(operationId))
            {
                continue;
            }

            var operation = _operations.Log.Find(operationId);
            if (operation is null || RuntimeOperations.IsTerminal(operation.State))
            {
                await _operations.ReleaseReservationAsync(operationId).ConfigureAwait(false);
                continue;
            }

            // Only an instance this launch created settles it: an older instance (a replace not yet resumed) does not.
            if (byModel.TryGetValue(reservation.ModelId, out var instance) && BelongsTo(instance, operation))
            {
                await SettleAsync(operationId, instance).ConfigureAwait(false);
            }
        }
    }

    private static bool BelongsTo(RuntimeStatus instance, OperationStatus operation) =>
        instance.OperationId is { } creator
            ? string.Equals(creator, operation.Id, StringComparison.Ordinal)
            : operation.Stage is OperationStages.Created or OperationStages.Started;

    private Task SettleAsync(string operationId, RuntimeStatus instance) => instance.State switch
    {
        ModelState.Ready => _operations.FinishLaunchAsync(operationId, OperationState.Succeeded, null, instance),
        ModelState.Failed => _operations.FinishLaunchAsync(operationId, OperationState.Failed, RuntimeOperations.InstanceFailed(instance), null),
        _ => Task.CompletedTask,
    };
}
