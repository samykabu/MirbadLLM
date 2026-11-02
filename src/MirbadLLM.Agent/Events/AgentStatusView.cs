using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// The event-sourced part of <c>GET /status</c> (design §4.3, §4.7): the latest runtimes, operations, downloads and
/// reservations as carried by the published events. <see cref="EventBus"/> applies every event under its state lock, and
/// <see cref="Build"/> runs under the same lock (through <see cref="IEventBus.Snapshot{T}"/>), so a snapshot contains
/// exactly the changes up to its watermark — no more, no less — even though the subsystems themselves (operation log,
/// driver, downloads) are updated asynchronously outside that lock.
/// <para>Consequence for publishers: every change that must appear in <c>/status</c> is published as an event carrying the
/// full new object (<see cref="RuntimeEvent"/>, <see cref="OperationEvent"/>, <see cref="DownloadEvent"/>,
/// <see cref="ReservationEvent"/>). State that exists before the first event (instances adopted at start, terminal
/// operations of earlier boots) is loaded once with <see cref="Seed"/> before the listener opens.</para>
/// <para>Not thread-safe on its own: only the bus (under its lock) and code inside <see cref="IEventBus.Snapshot{T}"/> touch it.</para>
/// </summary>
public sealed class AgentStatusView
{
    /// <summary>Terminal downloads kept for <c>/status</c> ("recently finished").</summary>
    public const int RecentDownloadCount = 100;

    private readonly Dictionary<string, RuntimeStatus> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationStatus> _activeOperations = new(StringComparer.Ordinal);
    private readonly LinkedList<OperationStatus> _recentOperations = new(); // newest first
    private readonly Dictionary<string, NodeDownloadStatus> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReservationInfo> _reservations = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads the state that predates the first event, atomically under <paramref name="bus"/>'s state lock and without a
    /// seq. Call once at start, before the listener opens (MN-025/MN-026); later changes must be published as events.
    /// </summary>
    public void Seed(
        IEventBus bus,
        IEnumerable<RuntimeStatus>? runtimes = null,
        IEnumerable<OperationStatus>? operations = null,
        IEnumerable<NodeDownloadStatus>? downloads = null,
        IEnumerable<ReservationInfo>? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Snapshot(_ =>
        {
            foreach (var runtime in runtimes ?? [])
            {
                _runtimes[runtime.ModelId] = runtime;
            }

            // Oldest first, so the newest terminal ones end up at the front of the recent list.
            foreach (var operation in (operations ?? []).OrderBy(o => o.EndedAt ?? o.StartedAt))
            {
                ApplyOperation(operation);
            }

            foreach (var download in downloads ?? [])
            {
                ApplyDownload(download);
            }

            foreach (var reservation in reservations ?? [])
            {
                _reservations[reservation.OperationId] = reservation;
            }

            return 0;
        });
    }

    /// <summary>Applies one published event. Called by <see cref="EventBus"/> under its state lock.</summary>
    internal void Apply(AgentEvent evt)
    {
        switch (evt)
        {
            case RuntimeEvent runtime when runtime.Runtime is null:
                _runtimes.Remove(runtime.ModelId);
                break;
            case RuntimeEvent { Runtime: { } status } runtime:
                _runtimes[runtime.ModelId] = status;
                break;
            case OperationEvent operation:
                ApplyOperation(operation.Operation);
                break;
            case DownloadEvent download:
                ApplyDownload(download.Download);
                break;
            case ReservationEvent { Released: true } reservation:
                _reservations.Remove(reservation.Reservation.OperationId);
                break;
            case ReservationEvent reservation:
                _reservations[reservation.Reservation.OperationId] = reservation.Reservation;
                break;
        }
    }

    /// <summary>
    /// Builds the snapshot for watermark <paramref name="seq"/>. Call only inside <see cref="IEventBus.Snapshot{T}"/>.
    /// Health and memory are not event-sourced: the caller probes them just before.
    /// </summary>
    public StatusSnapshot Build(string bootId, long seq, DateTimeOffset time, NodeHealth health, NodeMemorySnapshot memory) => new()
    {
        BootId = bootId,
        Seq = seq,
        Time = time,
        Health = health,
        Memory = memory,
        Reservations = [.. _reservations.Values.OrderBy(r => r.CreatedAt).ThenBy(r => r.OperationId, StringComparer.Ordinal)],
        Runtimes = [.. _runtimes.Values.OrderBy(r => r.ModelId, StringComparer.Ordinal)],
        Downloads = [.. _downloads.Values.OrderBy(d => d.StartedAt).ThenBy(d => d.Folder, StringComparer.Ordinal)],
        Operations =
        [
            .. _activeOperations.Values.OrderBy(o => o.StartedAt).ThenBy(o => o.Id, StringComparer.Ordinal),
            .. _recentOperations,
        ],
    };

    private void ApplyOperation(OperationStatus operation)
    {
        if (!operation.State.IsTerminal())
        {
            // A late progress/stage publish never re-opens an operation that already ended (W2 review MN-092).
            if (FindRecent(operation.Id) is null)
            {
                _activeOperations[operation.Id] = operation;
            }

            return;
        }

        RemoveRecent(operation.Id);

        _activeOperations.Remove(operation.Id);
        _recentOperations.AddFirst(operation);
        while (_recentOperations.Count > OperationRetention.StatusRecentCount)
        {
            _recentOperations.RemoveLast();
        }
    }

    private OperationStatus? FindRecent(string operationId)
    {
        foreach (var recent in _recentOperations)
        {
            if (string.Equals(recent.Id, operationId, StringComparison.Ordinal))
            {
                return recent;
            }
        }

        return null;
    }

    private void RemoveRecent(string operationId)
    {
        for (var node = _recentOperations.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.Id, operationId, StringComparison.Ordinal))
            {
                _recentOperations.Remove(node);
                return;
            }
        }
    }

    private void ApplyDownload(NodeDownloadStatus download)
    {
        _downloads[download.Folder] = download;
        if (!IsFinished(download.State))
        {
            return;
        }

        var finished = _downloads.Values.Where(d => IsFinished(d.State)).ToList();
        if (finished.Count <= RecentDownloadCount)
        {
            return;
        }

        foreach (var old in finished.OrderBy(d => d.EndedAt ?? d.StartedAt).Take(finished.Count - RecentDownloadCount))
        {
            _downloads.Remove(old.Folder);
        }
    }

    private static bool IsFinished(DownloadState state) =>
        state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;
}
