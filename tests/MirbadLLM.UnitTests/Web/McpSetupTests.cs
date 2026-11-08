using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Mcp;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>Access > MCP: every tool documented, per-client setup filled with this address, and a copyable setup prompt.</summary>
public sealed class McpSetupTests
{
    private static IRenderedComponent<McpSetup> Render(BunitContext ctx, string scheme = "http", string host = "spark")
    {
        var httpContext = new DefaultHttpContext { Request = { Scheme = scheme, Host = new HostString(host, 8080) } };
        return ctx.Render<McpSetup>(p => p.AddCascadingValue(httpContext));
    }

    [Fact]
    public void Catalog_SplitsReadAndOperatorTools_AndMarksDestructiveOnes()
    {
        Assert.Equal(10, McpToolCatalog.All.Count(t => t.ReadOnly));
        Assert.Equal(8, McpToolCatalog.All.Count(t => !t.ReadOnly));
        Assert.Equal(
            ["mirbad_stop_model", "mirbad_delete_model", "mirbad_cancel_job"],
            McpToolCatalog.All.Where(t => t.Destructive).Select(t => t.Name));

        McpToolInfo start = McpToolCatalog.All.Single(t => t.Name == "mirbad_start_model");
        Assert.Equal(["modelId", "force?"], start.Arguments);
    }

    [Fact]
    public void ListsEveryTool_WithItsDescriptionAndKey_AndMarksTheMcpTabCurrent()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        Assert.Equal("page", cut.Find("[data-testid=access-tab-mcp]").GetAttribute("aria-current"));
        Assert.Equal(McpToolCatalog.All.Count.ToString(), cut.Find("[data-testid=mcp-tool-count]").TextContent);
        Assert.Equal(McpToolCatalog.All.Count, cut.FindAll("[data-testid=mcp-tools] tbody tr").Count);
        foreach (McpToolInfo tool in McpToolCatalog.All)
        {
            string row = cut.Find($"[data-testid=mcp-tool-{tool.Name}]").TextContent;
            Assert.Contains(tool.Description, row, StringComparison.Ordinal);
            Assert.Contains(tool.ReadOnly ? "MCP Read" : "MCP Operator", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClientTabs_FillInThisAddress_AndNeverExpandTheKeyInTheShell()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        string codex = cut.Find("[data-testid=mcp-codex-snippet]").TextContent;
        Assert.Contains("url = \"http://spark:8080/mcp\"", codex, StringComparison.Ordinal);
        Assert.Contains("bearer_token_env_var = \"MIRBAD_MCP_KEY\"", codex, StringComparison.Ordinal);

        cut.Find("[data-testid=mcp-client-tabs-tab-claude-code]").Click();
        Assert.Contains(
            "claude mcp add --transport http --scope user mirbad http://spark:8080/mcp --header 'Authorization: Bearer ${MIRBAD_MCP_KEY}'",
            cut.Find("[data-testid=mcp-claude-code-snippet]").TextContent,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http", true)]
    [InlineData("https", false)]
    public void DesktopBridge_AllowsPlainHttp_OnlyForAnHttpAddress(string scheme, bool allowsHttp)
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, scheme);
        cut.Find("[data-testid=mcp-client-tabs-tab-claude-desktop]").Click();

        string config = cut.Find("[data-testid=mcp-claude-desktop-snippet]").TextContent;
        Assert.Equal(allowsHttp, config.Contains("--allow-http", StringComparison.Ordinal));
        Assert.Equal(allowsHttp, cut.Find("[data-testid=mcp-prompt-snippet]").TextContent.Contains("--allow-http", StringComparison.Ordinal));
        using var parsed = System.Text.Json.JsonDocument.Parse(config);
        Assert.Equal("npx", parsed.RootElement.GetProperty("mcpServers").GetProperty("mirbad").GetProperty("command").GetString());
    }

    [Fact]
    public void CopyPrompt_SendsTheExactTextWithThisAddress_AndKeepsTheAgentAwayFromTheKey()
    {
        using var ctx = new BunitContext();
        var module = ctx.JSInterop.SetupModule("./interop.js");
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        var cut = Render(ctx);

        cut.Find("[data-testid=copy-mcp-prompt]").Click();

        string prompt = (string)Assert.Single(module.Invocations["copyText"]).Arguments[0]!;
        Assert.Contains("URL: http://spark:8080/mcp", prompt, StringComparison.Ordinal);
        Assert.Contains("MIRBAD_MCP_KEY", prompt, StringComparison.Ordinal);
        Assert.Contains("Never ask me to paste the key", prompt, StringComparison.Ordinal);
        Assert.Contains("Call only mirbad_list_nodes", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectPage_NoLongerCarriesTheMcpPanel()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        var httpContext = new DefaultHttpContext { Request = { Scheme = "http", Host = new HostString("spark", 8080) } };

        var cut = ctx.Render<Connect>(p => p.AddCascadingValue(httpContext));

        Assert.Empty(cut.FindAll("[data-testid=mcp-connect]"));
        Assert.Equal("access/mcp", cut.Find("[data-testid=access-tab-mcp]").GetAttribute("href"));
    }
}
