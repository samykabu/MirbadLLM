using System.Globalization;
using System.Text;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// <c>POST /agent/update</c> (docs/multinode/agent-updates.md U3–U5): install the master's bundle of this build on this
/// node. The agent downloads it from the master's own bundle route (<see cref="BundlePath"/> on <see cref="BundlePort"/>
/// at the address the order came from), checks <see cref="Sha256"/> and <see cref="Size"/>, and hands it with
/// <see cref="Order"/> and <see cref="Signature"/> to the node's root update helper, which verifies the signature
/// against its own copy of the master's CA and runs the bundle's installer <c>upgrade</c>. Feature
/// <see cref="AgentFeatures.AgentUpdate"/>.
/// </summary>
public sealed record AgentUpdateRequest : AgentMutation
{
    /// <summary>The bundle's runtime id, e.g. <c>linux-x64</c>.</summary>
    public required string Rid { get; init; }

    /// <summary>The bundle's revision (BUILD-INFO), the build the agent becomes.</summary>
    public required string Version { get; init; }

    /// <summary>The bundle's SHA-256 (lower-case hex).</summary>
    public required string Sha256 { get; init; }

    /// <summary>The bundle's size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Path of the bundle on the master's main port, e.g. <c>/agent/bundles/linux-x64.tar.gz</c>.</summary>
    public required string BundlePath { get; init; }

    /// <summary>The master's main (HTTP) port, where it serves the bundles.</summary>
    public required int BundlePort { get; init; }

    /// <summary>The signed order text (<see cref="AgentUpdateOrder.Format"/>), passed to the helper as is.</summary>
    public required string Order { get; init; }

    /// <summary>Base64 ECDSA P-256 / SHA-256 signature (DER) of <see cref="Order"/>'s UTF-8 bytes by the master's CA.</summary>
    public required string Signature { get; init; }
}

/// <summary>An agent update's result: the build the agent runs after it.</summary>
public sealed record AgentUpdateResult
{
    /// <summary>The agent's informational version after the update, e.g. <c>1.0.0+826bc75fc222</c>.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>The last lines of the installer's output (from the helper), for a failed or rolled-back update.</summary>
    public string? Log { get; init; }
}

/// <summary>
/// The text the master signs and the node's update helper checks (docs/multinode/agent-updates.md U5): one
/// <c>key=value</c> per line after a header line, so a shell script can read it without a JSON parser. The helper accepts
/// it only for its own node uid, its own bundle rid, before <see cref="ExpiresAt"/>, and installs only a bundle with this
/// SHA-256 and size.
/// </summary>
public sealed record AgentUpdateOrder(string NodeUid, string Rid, string Version, string Sha256, long Size, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    public const string Header = "mirbadllm-agent-update 1";

    /// <summary>
    /// The header <see cref="Format"/> writes: the pre-rename one, because the update helper (and the agent) installed on a
    /// pre-rename node accepts only it, and that helper runs the update that replaces it. <see cref="Parse"/> accepts both.
    /// </summary>
    public const string WrittenHeader = LegacyNames.UpdateOrderHeader;

    /// <summary>The canonical text: header, then node-uid, rid, version, sha256, size, issued, expires (unix seconds), LF-terminated.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        text.Append(WrittenHeader).Append('\n');
        Line(text, "node-uid", NodeUid);
        Line(text, "rid", Rid);
        Line(text, "version", Version);
        Line(text, "sha256", Sha256);
        Line(text, "size", Size.ToString(CultureInfo.InvariantCulture));
        Line(text, "issued", IssuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Line(text, "expires", ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>Reads <see cref="Format"/>'s text back; null when it is not exactly that shape.</summary>
    public static AgentUpdateOrder? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Split('\n');
        if (lines.Length != 9 || lines[0] is not (Header or LegacyNames.UpdateOrderHeader) || lines[8].Length != 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines[1..8])
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !values.TryAdd(line[..eq], line[(eq + 1)..]))
            {
                return null;
            }
        }

        return values.TryGetValue("node-uid", out var uid) && values.TryGetValue("rid", out var rid)
            && values.TryGetValue("version", out var version) && values.TryGetValue("sha256", out var sha)
            && values.TryGetValue("size", out var size) && long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
            && values.TryGetValue("issued", out var issued) && long.TryParse(issued, NumberStyles.None, CultureInfo.InvariantCulture, out long i)
            && values.TryGetValue("expires", out var expires) && long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out long e)
            ? new AgentUpdateOrder(uid, rid, version, sha, bytes, DateTimeOffset.FromUnixTimeSeconds(i), DateTimeOffset.FromUnixTimeSeconds(e))
            : null;
    }

    private static void Line(StringBuilder text, string key, string value)
    {
        // A value can never break the line format (the helper reads it with a plain shell loop).
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException($"An update order's {key} cannot contain a line break or '='.", nameof(value));
        }

        text.Append(key).Append('=').Append(value).Append('\n');
    }
}
