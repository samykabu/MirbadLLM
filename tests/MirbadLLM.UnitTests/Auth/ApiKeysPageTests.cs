using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Auth;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the <c>/keys</c> page: listing, creating in the Add key dialog (showing the new key exactly
/// once), and revoking (via the in-app confirmation dialog, not <c>window.confirm</c>).</summary>
public sealed class ApiKeysPageTests
{
    private static Bunit.BunitContext MakeContext(FakeApiKeyStore store)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IApiKeyStore>(store);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public void ListsExistingKeys_WithPrefixAndStatus()
    {
        var store = new FakeApiKeyStore();
        var created = store.AddActiveKey("ssk_abcdefgh12345678", ApiKeyScope.Inference, "laptop");
        using var ctx = MakeContext(store);

        var cut = ctx.Render<ApiKeys>();

        var row = cut.Find($"[data-testid=key-row-{created.Id}]");
        Assert.Contains("laptop", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Inference", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Active", cut.Find($"[data-testid=status-{created.Id}]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyState_WhenNoKeysExist()
    {
        using var ctx = MakeContext(new FakeApiKeyStore());

        var cut = ctx.Render<ApiKeys>();

        Assert.NotNull(cut.Find("[data-testid=keys-empty]"));
        // The empty state offers the same Add key button (the only one on the page).
        Assert.NotNull(cut.Find("[data-testid=keys-empty] [data-testid=add-key]"));
        Assert.Single(cut.FindAll("[data-testid=add-key]"));
    }

    [Fact]
    public async Task CreatingAKey_ShowsTheFullKeyExactlyOnce()
    {
        var store = new FakeApiKeyStore();
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();

        cut.Find("[data-testid=field-key-name]").Change("laptop");
        cut.Find("[data-testid=create-key-form]").Submit();

        var shown = cut.Find("[data-testid=add-key-dialog] [data-testid=created-key-value]").TextContent;
        Assert.StartsWith("ssk_", shown, StringComparison.Ordinal);

        // The store now holds exactly one key, and the shown value isn't re-displayed anywhere else on
        // the page (e.g. the table only ever renders the short prefix, never the full key).
        var stored = Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Equal("laptop", stored.Name);
        Assert.DoesNotContain(shown, cut.Find($"[data-testid=key-row-{stored.Id}]").TextContent, StringComparison.Ordinal);

        // Done closes the dialog and the key is gone for good; opening it again shows an empty form.
        Assert.Equal("Done", cut.Find("[data-testid=add-key-done]").TextContent.Trim());
        cut.Find("[data-testid=add-key-done]").Click();
        Assert.Equal("false", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open"));
        Assert.DoesNotContain(shown, cut.Markup, StringComparison.Ordinal);
        cut.Find("[data-testid=add-key]").Click();
        Assert.Empty(cut.FindAll("[data-testid=created-key-value]"));
        Assert.Equal("", cut.Find("[data-testid=field-key-name]").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task ScopeSegments_CreateAMetricsKey_AndRevealItInsideTheDialog()
    {
        var store = new FakeApiKeyStore();
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();
        cut.Find("[data-testid=field-key-name]").Change("prometheus");
        cut.Find("[data-testid=key-scope-metrics]").Change("Metrics");
        cut.Find("[data-testid=create-key-form]").Submit();
        Assert.Equal(ApiKeyScope.Metrics, Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken)).Scope);
        Assert.NotNull(cut.Find("[data-testid=add-key-dialog] [data-testid=created-key-banner]"));
        Assert.Contains("shown once", cut.Find("[data-testid=created-key-banner]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeSegments_CreateMcpOperatorKey_AndDescribeMcpEndpoint()
    {
        var store = new FakeApiKeyStore();
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();
        cut.Find("[data-testid=field-key-name]").Change("automation");
        cut.Find("[data-testid=key-scope-mcp-operator]").Change("McpOperator");
        cut.Find("[data-testid=create-key-form]").Submit();

        Assert.Equal(ApiKeyScope.McpOperator, Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken)).Scope);
        Assert.Contains("/mcp", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeConfirmButton_RevokesTheKey_WithoutWindowConfirm()
    {
        var store = new FakeApiKeyStore();
        var created = store.AddActiveKey("ssk_torevoke1234567", ApiKeyScope.Metrics, "old-dashboard");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();

        // Revoke only opens the dialog; nothing is revoked until its confirm button is pressed.
        cut.Find($"[data-testid=revoke-{created.Id}]").Click();
        Assert.Equal("true", cut.Find("[data-testid=revoke-dialog]").GetAttribute("data-open"));
        Assert.True(Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken)).IsActive);
        Assert.Contains("401", cut.Find("[data-testid=revoke-dialog]").TextContent, StringComparison.Ordinal);

        cut.Find($"[data-testid=confirm-revoke-{created.Id}]").Click();

        var stored = Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.False(stored.IsActive);
        Assert.Contains("Revoked", cut.Find($"[data-testid=status-{created.Id}]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveKey_HasNoDeleteAction()
    {
        var store = new FakeApiKeyStore();
        var created = store.AddActiveKey("ssk_active12345678", ApiKeyScope.Inference, "laptop");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();

        Assert.Empty(cut.FindAll($"[data-testid=delete-{created.Id}]"));
    }

    [Fact]
    public async Task DeleteConfirmButton_RemovesTheRevokedKey_WithoutWindowConfirm()
    {
        var store = new FakeApiKeyStore();
        var created = store.AddRevokedKey("ssk_todelete123456", ApiKeyScope.Inference, "old-laptop");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();

        Assert.Empty(cut.FindAll($"[data-testid=revoke-{created.Id}]"));

        // Delete only opens the dialog; nothing is removed until its confirm button is pressed.
        cut.Find($"[data-testid=delete-{created.Id}]").Click();
        Assert.Equal("true", cut.Find("[data-testid=delete-dialog]").GetAttribute("data-open"));
        Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Contains("permanently", cut.Find("[data-testid=delete-dialog]").TextContent, StringComparison.Ordinal);
        Assert.Contains(created.Name, cut.Find("[data-testid=delete-dialog-body]").TextContent, StringComparison.Ordinal);

        cut.Find($"[data-testid=confirm-delete-{created.Id}]").Click();

        Assert.Empty(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.NotNull(cut.Find("[data-testid=keys-empty]"));
    }

    [Fact]
    public void NameField_IsDescribedByItsHintAndItsError_AndMarkedInvalidOnAFailedSubmit()
    {
        using var ctx = MakeContext(new FakeApiKeyStore());
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();

        var name = cut.Find("[data-testid=field-key-name]");
        Assert.Equal("key-name-hint key-name-error", name.GetAttribute("aria-describedby"));
        Assert.Null(name.GetAttribute("aria-invalid"));
        Assert.Equal("key-scope-hint", cut.Find("[data-testid=field-key-scope]").GetAttribute("aria-describedby"));

        cut.Find("[data-testid=create-key-form]").Submit();

        Assert.Equal("true", cut.Find("[data-testid=field-key-name]").GetAttribute("aria-invalid"));
        Assert.NotEmpty(cut.Find("[data-testid=add-key-dialog] #key-name-error").TextContent.Trim());
        Assert.Equal("true", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open")); // still open
    }

    [Fact]
    public async Task AddKey_OpensAModalDialog_LabelledByItsTitle_AndCancelClosesItWithoutCreating()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_abcdefgh12345678", ApiKeyScope.Inference, "laptop");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();

        var dialog = cut.Find("[data-testid=add-key-dialog]");
        Assert.Equal("false", dialog.GetAttribute("data-open"));
        Assert.Contains("1 active of 1", cut.Find(".section-head").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".section-head [data-testid=add-key]"));
        Assert.Empty(cut.FindAll(".keys-create, .keys-layout, [data-testid=vscode-config]"));

        cut.Find("[data-testid=add-key]").Click();
        dialog = cut.Find("[data-testid=add-key-dialog]");
        Assert.Equal("true", dialog.GetAttribute("data-open"));
        Assert.Equal("add-key-title", dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Add an API key", cut.Find("#add-key-title").TextContent);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "showDialog");

        cut.Find("[data-testid=field-key-name]").Change("other");
        cut.Find("[data-testid=add-key-cancel]").Click();
        Assert.Equal("false", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "closeDialog");
        Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Esc_ClosesTheDialog_AndForgetsAShownKey()
    {
        using var ctx = MakeContext(new FakeApiKeyStore());
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();
        cut.Find("[data-testid=field-key-name]").Change("laptop");
        cut.Find("[data-testid=create-key-form]").Submit();
        string shown = cut.Find("[data-testid=created-key-value]").TextContent;

        // Esc: the browser closes a modal <dialog> and raises 'cancel'.
        cut.Find("[data-testid=add-key-dialog]").TriggerEvent("oncancel", EventArgs.Empty);

        Assert.Equal("false", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open"));
        Assert.DoesNotContain(shown, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackdropClick_ClosesTheDialog_AndAClickInsideDoesNot()
    {
        using var ctx = MakeContext(new FakeApiKeyStore());
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();

        await cut.Find("[data-testid=add-key-dialog]").ClickAsync(new());

        Assert.Equal("false", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open"));
    }

    [Fact]
    public async Task ADuplicateName_IsRefusedInsideTheDialog()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_abcdefgh12345678", ApiKeyScope.Inference, "laptop");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();
        cut.Find("[data-testid=add-key]").Click();

        cut.Find("[data-testid=field-key-name]").Change("Laptop");
        cut.Find("[data-testid=create-key-form]").Submit();

        Assert.Contains("already named Laptop", cut.Find("[data-testid=add-key-dialog] [data-testid=add-key-error]").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid=add-key-dialog]").GetAttribute("data-open"));
        Assert.Empty(cut.FindAll("[data-testid=created-key-value]"));
        Assert.Single(await store.ListAsync(Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public void KeysTable_ScrollsInsideItsPanel_AndStacksByContainerWidth()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_abcdefgh12345678", ApiKeyScope.Inference, "laptop");
        using var ctx = MakeContext(store);
        var cut = ctx.Render<ApiKeys>();

        var table = cut.Find("[data-testid=keys-table]");
        Assert.Contains("data-table--stack-wide", table.ClassName, StringComparison.Ordinal);
        Assert.Contains("panel--scroll-x", table.ParentElement!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void SharesTheAccessHeader_WithApiKeysCurrent_AndTheActiveCountOnItsTab()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_abcdefgh12345678", ApiKeyScope.Inference, "laptop");
        store.AddActiveKey("ssk_bbbbbbbb12345678", ApiKeyScope.Metrics, "prometheus");
        using var ctx = MakeContext(store);

        var cut = ctx.Render<ApiKeys>();

        Assert.Equal("Access", cut.Find("[data-testid=page-title]").TextContent);
        Assert.Equal("page", cut.Find("[data-testid=access-tab-keys]").GetAttribute("aria-current"));
        Assert.Null(cut.Find("[data-testid=access-tab-connect]").GetAttribute("aria-current"));
        Assert.Equal("access", cut.Find("[data-testid=access-tab-connect]").GetAttribute("href"));
        Assert.Equal("2", cut.Find("[data-testid=access-tab-keys] .access-tab-count").TextContent);
    }
}
