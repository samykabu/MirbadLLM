namespace MirbadLLM.Orchestration.Fencing;

/// <summary>
/// Master fencing options (design §4.2), bound from <c>MirbadLLM:Fencing</c> (environment:
/// <c>MirbadLLM__Fencing__Takeover=true</c>).
/// </summary>
public sealed class FencingOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Fencing";

    /// <summary>Default <see cref="LockWait"/>.</summary>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Operator request for an explicit takeover (design §4.2 step 2), the alternative to creating
    /// <c>{DataPath}/takeover</c>. Meant for one run only: remove it once the takeover has completed, since each boot
    /// with it set asks for a takeover again.
    /// </summary>
    public bool Takeover { get; set; }

    /// <summary>
    /// One-shot id for <see cref="Takeover"/> (P2, frozen by MN-101, D73, closing the W1 review MN-090 note; implemented by
    /// MN-115): the takeover record is written with this <see cref="Core.TakeoverRecord.TakeoverId"/>, and once
    /// <c>master.json</c> holds a record with it, <see cref="Takeover"/> is ignored on later boots (logged, and the UI shows
    /// a red "remove MirbadLLM__Fencing__Takeover" warning while it is still set). Null with <see cref="Takeover"/> = true
    /// keeps P1 behaviour (each boot asks again). The flag file needs no id: it is deleted when the takeover completes.
    /// </summary>
    public string? TakeoverId { get; set; }

    /// <summary>
    /// How long <see cref="MasterIdentity.Open"/> keeps retrying the data directory lock before it gives up with a
    /// <see cref="MasterLockException"/>. A master restarted right behind its predecessor can find the lock still held
    /// for a moment (the OS releases a crashed process's lock, and an in-process host disposes its identity, a little
    /// after the restart begins); a real second master still fails once the wait is over. Zero disables the wait.
    /// </summary>
    public TimeSpan LockWait { get; set; } = DefaultLockWait;
}
