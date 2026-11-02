using System.Text;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Audit;

/// <summary>
/// Defence in depth for audit lines (design §10.4: "audit lines never contain secrets"). <see cref="AuditRecord"/> has no
/// secret field and every writer passes ids, codes and short details only; this pass still replaces anything that looks
/// like secret material with <c>***</c> before a line reaches disk: PEM blocks and private keys, mirbadllm API keys
/// (<c>ssk_…</c>), Hugging Face tokens (<c>hf_…</c>), bare base64url runs of 40+ characters (join and bootstrap
/// tokens, also after <c>=</c> as in <c>token=…</c>), and the value of a <c>password</c>/<c>passphrase</c>/<c>secret</c>/
/// <c>token</c>/<c>api key</c> assignment (<c>name=value</c> or <c>name: value</c>). Base64 SPKI pins (they end in
/// <c>=</c>) and hex ids stay readable. Control characters become spaces (one record = one line) and over-long values are
/// cut.
/// </summary>
public static partial class AuditRedaction
{
    /// <summary>The replacement for a redacted value.</summary>
    public const string Redacted = "***";

    /// <summary>Longest <see cref="AuditRecord.Detail"/> kept.</summary>
    public const int MaxDetailLength = 512;

    /// <summary>Longest value of every other text field kept.</summary>
    public const int MaxFieldLength = 256;

    /// <summary>The record with every text field sanitized.</summary>
    public static AuditRecord Sanitize(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            Actor = Clean(record.Actor, MaxFieldLength) ?? "",
            Action = Clean(record.Action, MaxFieldLength) ?? "",
            NodeId = Clean(record.NodeId, MaxFieldLength),
            ModelId = Clean(record.ModelId, MaxFieldLength),
            OperationId = Clean(record.OperationId, MaxFieldLength),
            Target = Clean(record.Target, MaxFieldLength),
            Result = Clean(record.Result, MaxFieldLength) ?? "",
            ErrorCode = Clean(record.ErrorCode, MaxFieldLength),
            Detail = Clean(record.Detail, MaxDetailLength),
        };
    }

    /// <summary>True when <paramref name="value"/> holds something <see cref="Sanitize"/> would redact.</summary>
    public static bool LooksSecret(string? value) =>
        value is not null && (value.Contains("-----BEGIN", StringComparison.Ordinal) ||
                              value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                              SecretToken().IsMatch(value) ||
                              SecretAssignment().IsMatch(value));

    private static string? Clean(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Contains("-----BEGIN", StringComparison.Ordinal) || value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return Redacted;
        }

        string text = SecretToken().Replace(SecretAssignment().Replace(value, "${name}${sep}" + Redacted), Redacted);
        if (text.Any(char.IsControl))
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                builder.Append(char.IsControl(c) ? ' ' : c);
            }

            text = builder.ToString();
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    // ssk_ API keys, hf_ tokens, and bare base64url runs of 40+ characters not followed by base64 padding (a pin is). An '='
    // before the run does not protect it ("token=<value>"): padding only ever ends a base64 value.
    [GeneratedRegex(@"ssk_[A-Za-z0-9_\-]{8,}|hf_[A-Za-z0-9]{8,}|(?<![A-Za-z0-9+/_\-])[A-Za-z0-9_\-]{40,}(?![A-Za-z0-9+/=_\-])", RegexOptions.CultureInvariant)]
    private static partial Regex SecretToken();

    // "password=…", "passphrase: …", "secret=…", "token=…", "api_key=…" (quoted or up to the next blank): the value goes.
    [GeneratedRegex(@"(?<name>\b(?:password|passphrase|secret|token|api[_\- ]?key)\b)(?<sep>\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();
}
