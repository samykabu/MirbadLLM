namespace MirbadLLM.Core;

/// <summary>
/// Names from before the rename to MirbadLLM (it was called SparkServe) that installed fleets still use: configuration
/// keys, container labels, files on disk and wire identifiers. New code writes the MirbadLLM names and reads these as a
/// fallback, so a running deployment and its installed agents keep working across the upgrade. Only shims use them.
/// </summary>
public static class LegacyNames
{
    /// <summary>The configuration section (<c>SparkServe:*</c>, <c>SparkServe__*</c> in the environment).</summary>
    public const string ConfigSection = "SparkServe";

    /// <summary>The agent's environment variable prefix (<c>SPARK_SERVE_AGENT_*</c>).</summary>
    public const string AgentEnvironmentPrefix = "SPARK_SERVE_AGENT_";

    /// <summary>The agent's configuration path variable.</summary>
    public const string AgentConfigVariable = "SPARK_SERVE_AGENT_CONFIG";

    /// <summary>The join token variable read by <c>join</c> and the installers.</summary>
    public const string JoinTokenVariable = "SPARK_SERVE_JOIN_TOKEN";

    /// <summary>Prefix of the container labels (<c>spark-serve.model</c>, <c>spark-serve.node</c>, ...).</summary>
    public const string LabelPrefix = "spark-serve.";

    /// <summary>The master's container name on the DGX.</summary>
    public const string MasterContainerName = "spark-serve";

    /// <summary>The DGX agent's container name.</summary>
    public const string AgentContainerName = "spark-serve-agent";

    /// <summary>The name an installed agent's service, unit, user and folders carry (<c>spark-serve-agent</c>).</summary>
    public const string AgentName = "spark-serve-agent";

    /// <summary>Marker file of an unfinished download in a model folder.</summary>
    public const string DownloadMarkerFile = ".spark-serve-download.json";

    /// <summary>Prefix of backup file names (<c>spark-serve-backup-{host}-{stamp}.json</c>).</summary>
    public const string BackupFilePrefix = "spark-serve-backup-";

    /// <summary>Prefix of the associated data that binds a backup's encrypted secrets to its schema (<c>spark-serve-backup/v{n}</c>).</summary>
    public const string BackupSchemaPrefix = "spark-serve-backup/v";

    /// <summary>Header of a signed agent-update order: the installed update helper of a pre-rename node accepts only it.</summary>
    public const string UpdateOrderHeader = "spark-serve-agent-update 1";

    /// <summary>Prefix of an agent bundle's folder (<c>spark-serve-agent-{version}-{rid}</c>), required by an installed pre-rename update helper.</summary>
    public const string BundleFolderPrefix = "spark-serve-agent-";

    /// <summary>Prefix of the agent-protocol HTTP headers (<c>SparkServe-Protocol</c>, ...): sent next to the new ones, read as a fallback.</summary>
    public const string HeaderPrefix = "SparkServe-";

    /// <summary>
    /// SPIFFE trust domain of the certificates. Every pre-rename agent requires exactly this domain in the master's
    /// certificate and in its own, so the master keeps issuing it and accepts both domains. Switch issuance to MirbadLLM's
    /// only once no pre-rename agent is left in any fleet.
    /// </summary>
    public const string SpiffeTrustDomain = "spark-serve";
}
