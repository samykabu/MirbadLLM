using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// The agent's host-services settings (design §4.3 host services, §9.1, §9.3, §10.3). Bound by the agent host (MN-026)
/// from the <c>Agent:HostServices</c> section; every value has a working default for the DGX container.
/// </summary>
public sealed class AgentHostServicesOptions
{
    /// <summary>Configuration section the agent host binds.</summary>
    public const string SectionName = "Agent:HostServices";

    /// <summary>mirbadllm-hostd's Unix socket (bind-mounted into the agent container, as for the controller today).</summary>
    public string HostdSocket { get; set; } = HostServicesOptions.DefaultHostAgentSocket;

    /// <summary>
    /// HTTP timeout of a hostd call. hostd bounds its own actions at 60 s; a call that is still unanswered after this is
    /// an ambiguous outcome (<c>Unknown</c>, design §4.4 item 8).
    /// </summary>
    public TimeSpan HostdTimeout { get; set; } = TimeSpan.FromSeconds(65);

    /// <summary>Docker Engine endpoint for non-model containers (same syntax as <c>MirbadLLM:DockerEndpoint</c>).</summary>
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>HTTP timeout of a Docker call (stop/restart wait up to the request's own timeout first).</summary>
    public TimeSpan DockerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>True when the node manages systemd units through hostd (<c>services.systemd</c>, Linux).</summary>
    public bool Systemd { get; set; } = true;

    /// <summary>
    /// Which service manager the operator-facing texts of this package name (<see cref="HostServiceManagers"/>):
    /// <c>systemd</c> through hostd on Linux, <c>scm</c> on the Windows PC (MN-239 item 1, D130), where "hostd", "no
    /// socket" and "masked" mean nothing. Only the words change: the wire codes (<c>hostd_unavailable</c>,
    /// <c>timeout</c>, <c>not_supported</c>) are the ones <see cref="AgentRoute.ErrorCodes"/> and operations.md pin.
    /// Set by <c>AddScmHostServices</c>; nothing else changes it.
    /// </summary>
    public string Manager { get; set; } = HostServiceManagers.Systemd;

    /// <summary>True when the node manages non-model Docker containers (<c>services.docker</c>).</summary>
    public bool Containers { get; set; } = true;

    /// <summary>True when the node forwards backup-share mounts to hostd (<c>backup-mount</c>, topology a).</summary>
    public bool BackupMount { get; set; } = true;

    /// <summary>
    /// True when hostd is expected on this node, so a missing socket is a Degraded reason (<c>hostd_missing</c>) from the
    /// first check on. Otherwise it is one only after hostd was seen during this agent's lifetime.
    /// </summary>
    public bool HostdExpected { get; set; }

    /// <summary>The master's container, never controlled by the agent (design §10.3).</summary>
    public string MasterContainerName { get; set; } = "mirbadllm";

    /// <summary>The agent's own container, never controlled by the agent (design §10.3).</summary>
    public string AgentContainerName { get; set; } = "mirbadllm-agent";
}
