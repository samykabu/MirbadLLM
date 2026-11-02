using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// Cheap launch-spec checks done before anything is reserved or recorded, so an unusable spec is answered 422
/// <c>spec_rejected</c> synchronously (design §4.3, §10.3). The driver repeats its own guardrails (image digests, mount
/// resolution, reserved labels) when it executes; this guard only refuses what can be decided from the request alone.
/// </summary>
internal static class LaunchSpecGuard
{
    /// <summary>Returns the refusal, or null when the spec passes.</summary>
    public static AgentException? Check(LaunchSpec? spec, bool hasDriver)
    {
        if (spec is null)
        {
            return Reject("The launch request has no spec.");
        }

        if (!hasDriver)
        {
            // The one refusal here that P3 added (D131 (7): a node with Docker off and no process delivery, or a
            // process spec sent to the DGX), so it is worded as the drivers' own validators word theirs —
            // "Launch spec rejected: {reason}." with the bare reason in the details. MN-239 H3: an operator who sees
            // this line next to a ProcessLaunchValidator or ContainerLaunchValidator refusal should not have to tell
            // two shapes apart. The other texts in this guard are P1/P2's, word for word, and stay that way.
            return Rejected("this node has no runtime driver for delivery '" + spec.Kind.ToString().ToLowerInvariant() + "'");
        }

        if (spec.Kind == LaunchDelivery.Container && string.IsNullOrWhiteSpace(spec.Image))
        {
            return Reject("A container spec needs an image.");
        }

        foreach (string reserved in ContainerLabels.Reserved)
        {
            if (spec.Labels.ContainsKey(reserved))
            {
                return Reject("Label '" + reserved + "' is reserved; the agent sets it.");
            }
        }

        foreach (var mount in spec.Mounts)
        {
            string? expectedTarget = mount.Volume switch
            {
                LogicalVolumes.Models => LogicalVolumes.ModelsTarget,
                LogicalVolumes.Cache => LogicalVolumes.CacheTarget,
                _ => null,
            };
            if (expectedTarget is null)
            {
                return Reject("Mount volume '" + mount.Volume + "' is not a logical volume (models, cache).");
            }

            if (!string.Equals(mount.Target, expectedTarget, StringComparison.Ordinal))
            {
                return Reject("Volume '" + mount.Volume + "' must be mounted at " + expectedTarget + ".");
            }

            if (mount.SubPath is { } sub && (Path.IsPathRooted(sub) || sub.Replace('\\', '/').Split('/').Contains("..")))
            {
                return Reject("Mount subPath '" + sub + "' must be a plain relative path.");
            }
        }

        return null;
    }

    private static AgentException Reject(string reason) =>
        new(StatusCodes.Status422UnprocessableEntity, new AgentError
        {
            Code = ErrorCodes.SpecRejected,
            Message = reason,
            Details = AgentJson.ToElement(new SpecRejectedDetails(reason)),
        });

    /// <summary>
    /// The refusal shape <c>ContainerLaunchValidator</c> and <c>ProcessLaunchValidator</c> use: <paramref name="reason"/>
    /// is a lower-case fragment without its period, the message prefixes it with "Launch spec rejected:" and the details
    /// carry the fragment alone. Used by this guard's P3 refusals only (MN-239 H3).
    /// </summary>
    private static AgentException Rejected(string reason) =>
        new(StatusCodes.Status422UnprocessableEntity, new AgentError
        {
            Code = ErrorCodes.SpecRejected,
            Message = $"Launch spec rejected: {reason}.",
            Details = AgentJson.ToElement(new SpecRejectedDetails(reason)),
        });
}
