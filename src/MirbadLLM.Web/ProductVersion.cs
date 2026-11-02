using MirbadLLM.Orchestration.Recovery;

namespace MirbadLLM.Web;

/// <summary>
/// The controller's build and the comparison with an agent's. Both carry their commit as an informational version
/// <c>1.0.0+&lt;commit&gt;</c> (SourceRevisionId: the master's and the DGX agent's images, every agent bundle). An agent on
/// another commit than the master is from another build: it still works while the protocol and feature flags agree, but
/// should be updated from the master's bundles.
/// </summary>
public static class ProductVersion
{
    private const int ShortRevision = 7;

    /// <summary>This controller's informational version, e.g. <c>1.0.0+826bc75fc222</c>.</summary>
    public static string Controller => BackupService.AppVersion;

    /// <summary>The commit of a version (after '+'); null when it carries none or the build did not know it (<c>unknown</c>).</summary>
    public static string? RevisionOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string revision = plus < 0 ? "" : version[(plus + 1)..].Trim();
        return revision.Length == 0 || string.Equals(revision, "unknown", StringComparison.OrdinalIgnoreCase) ? null : revision;
    }

    /// <summary><c>1.0.0 · 826bc75</c>: the version and the short commit, or the version alone.</summary>
    public static string Display(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return UiFormat.Missing;
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string number = plus < 0 ? version.Trim() : version[..plus].Trim();
        return RevisionOf(version) is { } revision
            ? $"{number} · {(revision.Length > ShortRevision && !revision.Contains('-', StringComparison.Ordinal) ? revision[..ShortRevision] : revision)}"
            : number;
    }

    /// <summary>
    /// True when the agent's build is the controller's, false when the commits differ, null when either does not say
    /// (an agent built without its commit, or a controller run from a checkout without one): then nothing is claimed.
    /// </summary>
    public static bool? SameBuild(string? agentVersion, string? controllerVersion = null)
    {
        string? agent = RevisionOf(agentVersion);
        string? controller = RevisionOf(controllerVersion ?? Controller);
        if (agent is null || controller is null)
        {
            return null;
        }

        // A short and a long form of the same commit match (BUILD-INFO and the images use 12 characters).
        return agent.StartsWith(controller, StringComparison.OrdinalIgnoreCase) || controller.StartsWith(agent, StringComparison.OrdinalIgnoreCase);
    }
}
