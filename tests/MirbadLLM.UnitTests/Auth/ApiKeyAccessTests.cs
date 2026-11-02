using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;

namespace MirbadLLM.UnitTests.Auth;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

/// <summary>Unit tests for the shared gating rules in <see cref="ApiKeyAccess"/> — the source of truth for
/// both the <c>/v1/*</c> router filter and the <c>/metrics</c> endpoint filter.</summary>
public sealed class ApiKeyAccessTests
{
    [Fact]
    public async Task CheckInferenceAsync_NoKeysNoLegacyNoFlag_IsOpen()
    {
        var store = new FakeApiKeyStore();

        var result = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: null, legacyApiKey: null, requireApiKey: false);

        Assert.Equal(ApiKeyAccessResult.OpenNoKeysConfigured, result);
    }

    [Fact]
    public async Task CheckInferenceAsync_RequireApiKeyFlagSet_DeniesWithNoKeys()
    {
        var store = new FakeApiKeyStore();

        var result = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: null, legacyApiKey: null, requireApiKey: true);

        Assert.Equal(ApiKeyAccessResult.Denied, result);
    }

    [Fact]
    public async Task CheckInferenceAsync_LegacyKeyConfigured_RequiresAKeyEvenWithoutManagedKeys()
    {
        var store = new FakeApiKeyStore();

        var missing = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: null, legacyApiKey: "legacy-secret", requireApiKey: false);
        var wrong = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "wrong", legacyApiKey: "legacy-secret", requireApiKey: false);
        var correct = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "legacy-secret", legacyApiKey: "legacy-secret", requireApiKey: false);

        Assert.Equal(ApiKeyAccessResult.Denied, missing);
        Assert.Equal(ApiKeyAccessResult.Denied, wrong);
        Assert.Equal(ApiKeyAccessResult.Allowed, correct);
    }

    [Fact]
    public async Task CheckInferenceAsync_OnceAManagedInferenceKeyExists_ItIsRequired()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_good", ApiKeyScope.Inference);

        var missing = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: null, legacyApiKey: null, requireApiKey: false);
        var wrong = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "ssk_bad", legacyApiKey: null, requireApiKey: false);
        var correct = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "ssk_good", legacyApiKey: null, requireApiKey: false);

        Assert.Equal(ApiKeyAccessResult.Denied, missing);
        Assert.Equal(ApiKeyAccessResult.Denied, wrong);
        Assert.Equal(ApiKeyAccessResult.Allowed, correct);
    }

    [Fact]
    public async Task CheckInferenceAsync_MetricsScopedKeyDoesNotSatisfyInference()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);

        var result = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "ssk_metrics", legacyApiKey: null, requireApiKey: false);

        // A Metrics key's mere existence doesn't force /v1 closed either — only an Inference key does.
        Assert.Equal(ApiKeyAccessResult.OpenNoKeysConfigured, result);
    }

    [Fact]
    public async Task CheckInferenceAsync_RevokedKeyIsRejected()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_gone", ApiKeyScope.Inference);
        store.Revoke("ssk_gone");

        var result = await ApiKeyAccess.CheckInferenceAsync(store, presentedKey: "ssk_gone", legacyApiKey: null, requireApiKey: false);

        Assert.Equal(ApiKeyAccessResult.Denied, result);
    }

    [Fact]
    public async Task CheckMetricsAsync_OpenUntilAMetricsKeyExists()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_inference-only", ApiKeyScope.Inference);

        // Only an Inference key exists — /metrics stays open regardless.
        var result = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey: null, legacyApiKey: null);

        Assert.Equal(ApiKeyAccessResult.OpenNoKeysConfigured, result);
    }

    [Fact]
    public async Task CheckMetricsAsync_OnceAMetricsKeyExists_EitherScopeSatisfiesIt()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);
        store.AddActiveKey("ssk_inference", ApiKeyScope.Inference);

        var missing = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey: null, legacyApiKey: null);
        var withMetricsKey = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey: "ssk_metrics", legacyApiKey: null);
        var withInferenceKey = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey: "ssk_inference", legacyApiKey: null);

        Assert.Equal(ApiKeyAccessResult.Denied, missing);
        Assert.Equal(ApiKeyAccessResult.Allowed, withMetricsKey);
        Assert.Equal(ApiKeyAccessResult.Allowed, withInferenceKey);
    }

    [Fact]
    public async Task CheckMetricsAsync_LegacyKeyOnlyCountsWhenPresented_NotAsATrigger()
    {
        var store = new FakeApiKeyStore();

        // The legacy option existing must not, by itself, close /metrics (only a Metrics key existing does).
        var result = await ApiKeyAccess.CheckMetricsAsync(store, presentedKey: null, legacyApiKey: "legacy-secret");

        Assert.Equal(ApiKeyAccessResult.OpenNoKeysConfigured, result);
    }
}
