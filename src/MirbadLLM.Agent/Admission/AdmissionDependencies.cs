using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// The runtime instances the reservation manager must see under its lock (design §4.8): instances still Starting hold the
/// start slot like today's <c>EnsureNoOtherModelStartingAsync</c>, and every instance's host port is in use (today's
/// <c>AllocatePortAsync</c> scans all labelled containers, exited ones included).
/// </summary>
public interface IRuntimeInstanceSource
{
    /// <summary>Every runtime instance on the node, in any state.</summary>
    Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default);
}

/// <summary><see cref="IRuntimeInstanceSource"/> over the node's runtime drivers (container driver, MN-015).</summary>
/// <param name="drivers">Registered drivers.</param>
public sealed class RuntimeDriverInstanceSource(IEnumerable<IRuntimeDriver> drivers) : IRuntimeInstanceSource
{
    private readonly IRuntimeDriver[] _drivers = [.. drivers];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<RuntimeStatus>();
        foreach (var driver in _drivers)
        {
            all.AddRange(await driver.ListAsync(ct).ConfigureAwait(false));
        }

        return all;
    }
}

/// <summary>
/// Persists a reservation with its operation (design §4.8: "persisted with the operation") so it can be rebuilt after a
/// restart. Called under the admission lock before the reservation becomes visible; if it throws, the reservation is not
/// taken and the exception propagates (the handler answers 503 <c>state_unavailable</c>).
/// <para>The agent composition (MN-026) passes <c>null</c>: MN-031 reserves <b>before</b> the durable accept (design §4.9)
/// and persists the reservation itself in the operation's <c>admitted</c> stage record, so an implementation over the
/// operation log would write to an operation that does not exist yet (W2 review MN-092 removed that adapter).</para>
/// </summary>
public interface IReservationPersistence
{
    /// <summary>Durably records <paramref name="reservation"/> with its operation.</summary>
    Task SaveAsync(ReservationInfo reservation, CancellationToken ct = default);
}

/// <summary>A reservation to rebuild after a restart, with the admission inputs when the operation payload still has them.</summary>
/// <param name="Reservation">The persisted reservation (or one derived from an instance still Starting).</param>
/// <param name="Admission">The launch's admission inputs; null = unknown (re-checks then use <see cref="AdmissionOptions.DefaultReserveGiB"/>, no force).</param>
public sealed record RebuiltReservation(ReservationInfo Reservation, AdmissionRequest? Admission = null);
