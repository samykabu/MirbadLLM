using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Fencing;

/// <summary>
/// The agent's fencing state (design §4.2 layer 2), persisted in <c>{stateDirectory}/fencing.json</c>
/// (<see cref="FencingStateDocument"/>). Decision table, serialized across callers:
/// <list type="table">
/// <item><term>nothing accepted yet</term><description>persist, then <see cref="FencingDecision.Accepted"/></description></item>
/// <item><term>lower epoch</term><description><see cref="FencingDecision.StaleMaster"/> (409 <c>stale_master</c>)</description></item>
/// <item><term>equal epoch, same instance and boot</term><description><see cref="FencingDecision.Accepted"/>, nothing written</description></item>
/// <item><term>equal epoch, other instance or boot</term><description><see cref="FencingDecision.CompetingMaster"/> (409 <c>competing_master</c>): the first master at an epoch wins</description></item>
/// <item><term>higher epoch</term><description>persist (flushed, renamed), then <see cref="FencingDecision.Accepted"/></description></item>
/// </list>
/// A triple is visible through <see cref="Accepted"/> and <see cref="IsCurrent"/> only after it is on disk. A failed
/// write leaves the previous state in place and throws <see cref="AgentException"/> 503 <c>state_unavailable</c>. A
/// corrupt or unreadable file stops the agent at startup (<see cref="InvalidDataException"/>) instead of silently
/// forgetting the accepted epoch, which would let an older master back in.
/// </summary>
public sealed class FencingState : IFencingState, IDisposable
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MasterTriple? _accepted;

    /// <summary>Loads the persisted triple from <paramref name="stateDirectory"/> (creating the directory if needed).</summary>
    public FencingState(string stateDirectory, TimeProvider? time = null, ILogger<FencingState>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, FencingStateDocument.FileName);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _accepted = Read(_path);
        if (_accepted is not null)
        {
            FencingLog.Loaded(_logger, _accepted.InstanceId, _accepted.Epoch, _accepted.BootId);
        }
    }

    /// <summary>Path of the state file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public MasterTriple? Accepted => Volatile.Read(ref _accepted);

    /// <inheritdoc />
    public async Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (!IsValidId(presented.InstanceId) || !IsValidId(presented.BootId) || presented.Epoch < 0)
        {
            throw new ArgumentException("The master triple has an empty or malformed id or a negative epoch.", nameof(presented));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MasterTriple? accepted = _accepted;
            FencingDecision decision = Decide(accepted, presented);
            if (decision != FencingDecision.Accepted)
            {
                FencingLog.Refused(_logger, presented.InstanceId, presented.Epoch, presented.BootId, decision.ToString(),
                    accepted!.InstanceId, accepted.Epoch, accepted.BootId);
                return new FencingCheck(decision, accepted);
            }

            if (accepted is not null && accepted.SameBoot(presented))
            {
                return new FencingCheck(FencingDecision.Accepted, accepted);
            }

            // Higher epoch (or the first master ever): on disk first, then visible.
            MasterTriple copy = new(presented.InstanceId, presented.Epoch, presented.BootId);
            Persist(copy);
            Volatile.Write(ref _accepted, copy);
            FencingLog.Accepted(_logger, copy.InstanceId, copy.Epoch, copy.BootId, accepted?.Epoch);
            return new FencingCheck(FencingDecision.Accepted, copy);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool IsCurrent(MasterTriple operationMaster)
    {
        ArgumentNullException.ThrowIfNull(operationMaster);
        return Volatile.Read(ref _accepted) is { } accepted && accepted.SameBoot(operationMaster);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The pure decision for <paramref name="presented"/> against <paramref name="accepted"/> (no side effects).</summary>
    internal static FencingDecision Decide(MasterTriple? accepted, MasterTriple presented)
    {
        if (accepted is null || presented.Epoch > accepted.Epoch)
        {
            return FencingDecision.Accepted;
        }

        if (presented.Epoch < accepted.Epoch)
        {
            return FencingDecision.StaleMaster;
        }

        return accepted.SameBoot(presented) ? FencingDecision.Accepted : FencingDecision.CompetingMaster;
    }

    private void Persist(MasterTriple triple)
    {
        FencingStateDocument document = new()
        {
            MasterInstanceId = triple.InstanceId,
            Epoch = triple.Epoch,
            BootId = triple.BootId,
            AcceptedAt = _time.GetUtcNow(),
        };

        string temp = _path + ".tmp";
        try
        {
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, FencingStateJson.Options);
                stream.Flush(flushToDisk: true);
            }

            // On Windows (dev/test) a scanner briefly holding the target can make the replace fail; retry a few times.
            // On Linux the rename is atomic and succeeds first time.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, _path, overwrite: true);
                    DirectorySync.FlushParent(_path); // a lost rename would re-admit a lower epoch (D45)
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20 * attempt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FencingLog.PersistFailed(_logger, ex, _path);
            throw new AgentException(503, new AgentError
            {
                Code = ErrorCodes.StateUnavailable,
                Message = "The agent could not persist the accepted master; nothing was changed.",
            }, innerException: ex);
        }
    }

    private static MasterTriple? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        FencingStateDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<FencingStateDocument>(File.ReadAllBytes(path), FencingStateJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{path}' is not valid fencing state ({ex.Message}). It is not reset automatically: forgetting the accepted " +
                "epoch would let an older master drive this node. Restore it, or delete it only after confirming which master is current.", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException($"'{path}' is empty. Restore it, or delete it only after confirming which master is current.");
        }

        if (document.SchemaVersion > FencingStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"'{path}' has schema version {document.SchemaVersion}; this agent reads up to {FencingStateDocument.CurrentSchemaVersion}.");
        }

        if (!IsValidId(document.MasterInstanceId) || !IsValidId(document.BootId) || document.Epoch < 0)
        {
            throw new InvalidDataException($"'{path}' has an invalid master instance id, boot id or epoch.");
        }

        return new MasterTriple(document.MasterInstanceId, document.Epoch, document.BootId);
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains('/', StringComparison.Ordinal) && !id.Any(char.IsWhiteSpace);
}
