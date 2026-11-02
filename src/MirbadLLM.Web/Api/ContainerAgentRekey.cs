namespace MirbadLLM.Web.Api;

/// <summary>
/// The re-pair procedure of the DGX's <b>container</b> agent (topology a, <c>deploy/compose.multinode.yaml</c>; MN-124,
/// D105): the bootstrap node enrolls once through the bootstrap bundle, which the master stops writing once the node is
/// enrolled, so an agent whose state volume (<c>mirbadllm-agent-state</c>) was lost, or whose certificate expired, can
/// only come back with <c>join --rekey</c> run inside the agent image against that volume. The PC installer line
/// (<see cref="LinuxInstallerLine"/>) does not apply there: it would install a second, systemd agent on the DGX.
/// <para>Run in the DGX's deploy checkout. The images are the running containers' (the CI deploy names them by commit; the
/// compose defaults are dev tags). The token is read at a prompt into <see cref="LinuxInstallerLine.TokenVariable"/> and
/// passed to the one-off container by name, so it never appears on a command line. The enroll URL is the master's
/// listener on the <c>mirbadllm</c> Docker network, as for the bootstrap bundle.</para>
/// </summary>
public static class ContainerAgentRekey
{
    /// <summary>The enroll listener as the agent container reaches it (the bootstrap bundle's endpoint).</summary>
    public const string NetworkEnrollUrl = "https://mirbadllm:8443";

    private const string Compose = "docker compose -f deploy/compose.multinode.yaml --env-file deploy/.env";

    /// <summary>The shell lines, in order (no token in any of them).</summary>
    public static IReadOnlyList<string> Lines(string nodeId, string nodeUid, string caHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(caHash);
        return
        [
            "export MIRBADLLM_IMAGE=$(docker inspect -f '{{.Config.Image}}' mirbadllm)",
            "export MIRBADLLM_AGENT_IMAGE=$(docker inspect -f '{{.Config.Image}}' mirbadllm-agent)",
            $"{Compose} stop agent",
            $"read -rsp 'Rekey token: ' {LinuxInstallerLine.TokenVariable} && export {LinuxInstallerLine.TokenVariable} && echo",
            $"{Compose} run --rm --no-deps -e {LinuxInstallerLine.TokenVariable} agent join --rekey --enroll-url {NetworkEnrollUrl} " +
                $"--ca-hash '{caHash}' --node-id '{nodeId}' --node-uid '{nodeUid}'",
            $"unset {LinuxInstallerLine.TokenVariable}",
            $"{Compose} up -d --no-build --no-deps agent",
        ];
    }

    /// <summary>The lines as one script.</summary>
    public static string Script(string nodeId, string nodeUid, string caHash) => string.Join('\n', Lines(nodeId, nodeUid, caHash));
}
