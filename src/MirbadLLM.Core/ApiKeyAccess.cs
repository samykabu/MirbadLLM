namespace MirbadLLM.Core;

/// <summary>Outcome of an API-key access check for a gated endpoint group (<c>/v1/*</c> or <c>/metrics</c>).</summary>
public enum ApiKeyAccessResult
{
    /// <summary>No key of the scope that gates this endpoint has ever been created — the endpoint stays
    /// open regardless of what (if anything) was presented.</summary>
    OpenNoKeysConfigured,

    /// <summary>A valid, active key of an allowed scope was presented (or the legacy option key matched).</summary>
    Allowed,

    /// <summary>Keys exist (or auth is otherwise required) but no valid key of an allowed scope was presented.</summary>
    Denied,
}

/// <summary>
/// Shared API-key gating rules, used by both the <c>/v1/*</c> router filter (<c>MirbadLLM.Router</c>) and
/// the <c>/metrics</c> endpoint filter (<c>MirbadLLM.Orchestration</c>) so the "open until the first key
/// exists" and legacy-key rules live in exactly one place.
/// </summary>
public static class ApiKeyAccess
{
    /// <summary>
    /// <c>/v1/*</c> rule: open while no Inference key has ever been created and <paramref name="requireApiKey"/>
    /// is false; once either is true, an active Inference key (or the legacy <paramref name="legacyApiKey"/>)
    /// is required. A Metrics-scoped key never satisfies this check. Once an Inference key has ever been
    /// created, the requirement sticks — revoking your only key does not silently reopen <c>/v1</c>.
    /// </summary>
    public static async Task<ApiKeyAccessResult> CheckInferenceAsync(
        IApiKeyStore store,
        string? presentedKey,
        string? legacyApiKey,
        bool requireApiKey,
        CancellationToken ct = default)
    {
        bool legacyConfigured = !string.IsNullOrEmpty(legacyApiKey);
        bool everHadInferenceKey = await store.HasAnyKeyAsync(ApiKeyScope.Inference, ct).ConfigureAwait(false);
        bool required = requireApiKey || legacyConfigured || everHadInferenceKey;

        if (!string.IsNullOrEmpty(presentedKey))
        {
            if (legacyConfigured && ConstantTimeCompare.Equals(presentedKey, legacyApiKey!))
            {
                return ApiKeyAccessResult.Allowed;
            }

            var record = await store.FindByPresentedKeyAsync(presentedKey, ct).ConfigureAwait(false);
            if (record is { Scope: ApiKeyScope.Inference })
            {
                return ApiKeyAccessResult.Allowed;
            }
        }

        return required ? ApiKeyAccessResult.Denied : ApiKeyAccessResult.OpenNoKeysConfigured;
    }

    /// <summary>
    /// <c>/metrics</c> rule: open while no Metrics key has ever been created; once one has, either a Metrics
    /// or an Inference key (including the legacy option key, which is treated as an Inference key) is
    /// required. As with <see cref="CheckInferenceAsync"/>, revoking your only Metrics key does not
    /// silently reopen <c>/metrics</c>.
    /// </summary>
    public static async Task<ApiKeyAccessResult> CheckMetricsAsync(
        IApiKeyStore store,
        string? presentedKey,
        string? legacyApiKey,
        CancellationToken ct = default)
    {
        bool everHadMetricsKey = await store.HasAnyKeyAsync(ApiKeyScope.Metrics, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(presentedKey))
        {
            bool legacyConfigured = !string.IsNullOrEmpty(legacyApiKey);
            if (legacyConfigured && ConstantTimeCompare.Equals(presentedKey, legacyApiKey!))
            {
                return ApiKeyAccessResult.Allowed;
            }

            var record = await store.FindByPresentedKeyAsync(presentedKey, ct).ConfigureAwait(false);
            if (record is { Scope: ApiKeyScope.Inference or ApiKeyScope.Metrics })
            {
                return ApiKeyAccessResult.Allowed;
            }
        }

        return everHadMetricsKey ? ApiKeyAccessResult.Denied : ApiKeyAccessResult.OpenNoKeysConfigured;
    }
}
