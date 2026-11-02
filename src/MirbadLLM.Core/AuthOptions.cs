namespace MirbadLLM.Core;

/// <summary>
/// Authentication knobs, bound from the same <c>MirbadLLM</c> configuration section as
/// <see cref="MirbadLLMOptions"/> (kept separate: <see cref="MirbadLLMOptions"/> itself is frozen for
/// Phase 1 — see <c>docs/architecture.md</c>).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Admin cookie-login password (env <c>MirbadLLM__AdminPassword</c>). Empty/null disables
    /// admin login entirely: the UI and <c>/api/*</c> stay open, and pages show a warning banner.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>When true, <c>/v1/*</c> requires an active Inference API key even before one has ever been
    /// created (normally <c>/v1/*</c> stays open until the first Inference key exists).</summary>
    public bool RequireApiKey { get; set; }
}
