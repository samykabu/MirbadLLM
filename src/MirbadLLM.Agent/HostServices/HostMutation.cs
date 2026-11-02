using System.Text.Json;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// One host-service mutation (design §4.3 host services): its operation kind (<see cref="OperationKinds"/>), target (unit
/// name, container name, the unit to whitelist, or <see cref="BackupMountTarget"/>) and typed request body.
/// </summary>
/// <param name="Kind">Operation kind.</param>
/// <param name="Target">Target recorded with the operation.</param>
/// <param name="Request">The request (envelope + kind-specific fields).</param>
public sealed record HostMutation(string Kind, string Target, AgentMutation Request)
{
    /// <summary>The target recorded for backup-mount operations.</summary>
    public const string BackupMountTarget = "backup-mount";

    /// <summary>Every host-service operation kind this package executes and recovers.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [.. OperationKinds.DesiredState, .. OperationKinds.Unverifiable];

    /// <summary>True when the kind is reconciled by reading the current state after a crash (design §4.4 item 8).</summary>
    public bool IsDesiredState => OperationKinds.DesiredState.Contains(Kind);

    /// <summary>True for units through hostd (services and the unit whitelist).</summary>
    public bool IsUnit => IsUnitKind(Kind);

    /// <summary>True for non-model containers through Docker.</summary>
    public bool IsContainer => IsContainerKind(Kind);

    /// <summary>True for the backup-share mount through hostd.</summary>
    public bool IsBackupMount => IsBackupMountKind(Kind);

    /// <summary>True for a unit kind (services and the unit whitelist, feature <c>services.systemd</c>).</summary>
    public static bool IsUnitKind(string kind) => kind is OperationKinds.ServiceStart or OperationKinds.ServiceStop
        or OperationKinds.ServiceRestart or OperationKinds.ServiceAutostart or OperationKinds.WhitelistAdd
        or OperationKinds.WhitelistUpdate or OperationKinds.WhitelistRemove;

    /// <summary>True for a container kind (feature <c>services.docker</c>).</summary>
    public static bool IsContainerKind(string kind) => kind is OperationKinds.ContainerStart or OperationKinds.ContainerStop
        or OperationKinds.ContainerRestart or OperationKinds.ContainerAutostart;

    /// <summary>True for a backup-mount kind (feature <c>backup-mount</c>).</summary>
    public static bool IsBackupMountKind(string kind) => kind is OperationKinds.BackupMountSet or OperationKinds.BackupMountRemove;

    /// <summary>The request type of a host-service kind; null for any other kind.</summary>
    public static Type? RequestTypeOf(string kind) => kind switch
    {
        OperationKinds.ServiceStart or OperationKinds.ServiceStop or OperationKinds.ServiceRestart => typeof(HostServiceActionRequest),
        OperationKinds.ServiceAutostart => typeof(HostServiceAutostartRequest),
        OperationKinds.WhitelistAdd => typeof(HostWhitelistAddRequest),
        OperationKinds.WhitelistUpdate => typeof(HostWhitelistUpdateRequest),
        OperationKinds.WhitelistRemove => typeof(HostWhitelistRemoveRequest),
        OperationKinds.ContainerStart or OperationKinds.ContainerStop or OperationKinds.ContainerRestart => typeof(HostContainerActionRequest),
        OperationKinds.ContainerAutostart => typeof(HostContainerAutostartRequest),
        OperationKinds.BackupMountSet => typeof(BackupMountSetRequest),
        OperationKinds.BackupMountRemove => typeof(BackupMountRemoveRequest),
        _ => null,
    };

    /// <summary>
    /// Rebuilds a mutation from a stored (redacted) payload for recovery. Null when the kind is not a host-service kind or
    /// the payload cannot be read.
    /// </summary>
    public static HostMutation? FromPayload(string kind, string target, JsonElement? payload)
    {
        if (RequestTypeOf(kind) is not { } type || payload is not { ValueKind: JsonValueKind.Object } body)
        {
            return null;
        }

        try
        {
            return body.Deserialize(type, AgentJson.Options) is AgentMutation request ? new HostMutation(kind, target, request) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces every <see cref="Protocol.SecretFieldNames"/> value (at any depth, case-insensitive) with
    /// <see cref="Protocol.RedactedValue"/>. The operation log redacts too (MN-013); doing it here as well means the share
    /// password never leaves the request handler, whatever log implementation is plugged in. Delegates to the shared
    /// <see cref="OperationPayload.Redact"/> (D38).
    /// </summary>
    public static JsonElement Redact(JsonElement payload) => OperationPayload.Redact(payload);
}
