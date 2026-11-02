namespace MirbadLLM.Core;

/// <summary>
/// ASP.NET Data Protection names from before the rename to MirbadLLM. They key existing ciphertext (saved tokens, the
/// backup destination's credentials, login cookies), so they must never change, or deployed data no longer decrypts.
/// </summary>
public static class LegacyProtectionPurposes
{
    /// <summary>Application name given to <c>SetApplicationName</c>; isolates the key ring and the cookie protectors.</summary>
    public const string ApplicationName = "spark-serve";

    /// <summary>Purpose of the saved tokens in <c>tokens.json</c>.</summary>
    public const string SavedTokens = "SparkServe.SavedTokens.v1";

    /// <summary>Purpose of the backup destination's credentials in <c>backup-destination.json</c>.</summary>
    public const string BackupDestination = "SparkServe.BackupDestination.v1";
}
