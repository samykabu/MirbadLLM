using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

/// <summary>The controller's and the agents' builds in the portal: display, comparison and the Nodes alert.</summary>
public sealed class ProductVersionTests
{
    [Theory]
    [InlineData("1.0.0+826bc75fc222", "826bc75fc222")]
    [InlineData("1.0.0+2d71c7d97d84-dirty", "2d71c7d97d84-dirty")]
    [InlineData("1.0.0+unknown", null)]
    [InlineData("1.0.0", null)]
    [InlineData(null, null)]
    public void TheRevisionIsWhatFollowsThePlus(string? version, string? revision) =>
        Assert.Equal(revision, ProductVersion.RevisionOf(version));

    [Theory]
    [InlineData("1.0.0+826bc75fc222", "1.0.0 · 826bc75")]
    [InlineData("1.0.0+2d71c7d97d84-dirty", "1.0.0 · 2d71c7d97d84-dirty")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData(null, "—")]
    public void TheDisplayShowsTheVersionAndTheShortCommit(string? version, string shown) =>
        Assert.Equal(shown, ProductVersion.Display(version));

    [Theory]
    [InlineData("1.0.0+826bc75fc222", "1.0.0+826bc75fc222", true)]
    [InlineData("1.0.0+826bc75", "1.0.0+826bc75fc222", true)]
    [InlineData("1.0.0+2d71c7d97d84", "1.0.0+826bc75fc222", false)]
    [InlineData("1.0.0", "1.0.0+826bc75fc222", null)]
    [InlineData("1.0.0+826bc75fc222", "1.0.0", null)]
    [InlineData("1.0.0+826bc75fc222", "1.0.0+unknown", null)]
    public void TwoBuildsCompareByCommitAndNothingIsClaimedWithoutOne(string agent, string controller, bool? same) =>
        Assert.Equal(same, ProductVersion.SameBuild(agent, controller));

    [Fact]
    public void ANodeOnAnotherCommitGetsTheBuildAlertButAPendingOneDoesNot()
    {
        var row = new Nodes.NodeRow
        {
            Id = "devpc",
            Name = "devpc",
            State = NodeState.Online,
            AgentVersion = "1.0.0+2d71c7d97d84",
            AgentProtocol = Protocol.Range,
        };

        Assert.True(Nodes.AgentBuildDiffers(row, "1.0.0+826bc75fc222"));
        Assert.False(Nodes.AgentBuildDiffers(row, "1.0.0+2d71c7d97d84"));
        Assert.False(Nodes.AgentBuildDiffers(row with { State = NodeState.PendingApproval }, "1.0.0+826bc75fc222"));
        Assert.False(Nodes.AgentBuildDiffers(row with { AgentVersion = "1.0.0" }, "1.0.0+826bc75fc222"));
    }
}
