using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web.Components.Pages;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Web;

/// <summary>Access > Tokens: saving a token by name, never rendering its value, renaming, replacing and deleting it.</summary>
public sealed class TokensPageTests : IDisposable
{
    private const string Value = "hf_pageTokenSecret0123456789abcd";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JsonTokenStore _store;

    public TokensPageTests() =>
        _store = new JsonTokenStore(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), new EphemeralDataProtectionProvider(), TimeProvider.System);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ITokenStore>(_store);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public async Task Save_ListsTheTokenByNameWithItsHint_AndNeverRendersTheValue()
    {
        using var ctx = Context();
        var cut = ctx.Render<Tokens>();
        cut.WaitForElement("[data-testid=tokens-empty]", Wait);

        cut.Find("[data-testid=field-token-name]").Change("hf-personal");
        cut.Find("[data-testid=field-token-value]").Change(Value);
        cut.Find("[data-testid=token-form]").Submit();

        var saved = Assert.Single(await _store.ListAsync(Ct));
        var row = cut.WaitForElement($"[data-testid=token-row-{saved.Id}]", Wait);
        Assert.Contains("hf-personal", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("…abcd", row.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, cut.Markup, StringComparison.Ordinal);
        Assert.Equal("", cut.Find("[data-testid=field-token-value]").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task ADuplicateName_IsRefusedWithAMessage()
    {
        await _store.AddAsync("hf", Value, Ct);
        using var ctx = Context();
        var cut = ctx.Render<Tokens>();

        cut.Find("[data-testid=field-token-name]").Change("HF");
        cut.Find("[data-testid=field-token-value]").Change("hf_otherValue000000000000wxyz");
        cut.Find("[data-testid=token-form]").Submit();

        Assert.Contains("already exists", cut.WaitForElement("[data-testid=tokens-error]", Wait).TextContent, StringComparison.Ordinal);
        Assert.Single(await _store.ListAsync(Ct));
    }

    [Fact]
    public async Task Edit_RenamesAndKeepsTheValueWhenLeftEmpty_AndDeleteRemovesIt()
    {
        var saved = await _store.AddAsync("hf", Value, Ct);
        using var ctx = Context();
        var cut = ctx.Render<Tokens>();

        cut.WaitForElement($"[data-testid=edit-token-{saved.Id}]", Wait).Click();
        cut.Find("[data-testid=field-token-name]").Change("hf-work");
        cut.Find("[data-testid=token-form]").Submit();

        cut.WaitForAssertion(() => Assert.Contains("hf-work", cut.Find($"[data-testid=token-row-{saved.Id}]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(Value, await _store.GetValueAsync(saved.Id, Ct));

        cut.Find($"[data-testid=delete-token-{saved.Id}]").Click();
        cut.WaitForElement($"[data-testid=confirm-delete-token-{saved.Id}]", Wait).Click();
        cut.WaitForElement("[data-testid=tokens-empty]", Wait);
        Assert.Empty(await _store.ListAsync(Ct));
    }
}
