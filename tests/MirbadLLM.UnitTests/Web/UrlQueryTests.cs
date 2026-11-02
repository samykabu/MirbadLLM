using Microsoft.AspNetCore.Components;
using MirbadLLM.Web.Components.Ui;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Web;

/// <summary>UrlQuery: query updates made in the same event (a tag toggle plus the pager going back to page 1) build on
/// each other even though, on a live circuit, NavigationManager.Uri only moves once the browser confirms.</summary>
public sealed class UrlQueryTests
{
    /// <summary>Like the interactive-server NavigationManager: NavigateTo only queues; Uri changes when the browser answers.</summary>
    private sealed class DeferredNavigationManager : NavigationManager
    {
        public DeferredNavigationManager(string uri) => Initialize("http://spark/", uri);

        public List<string> Requested { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options) => Requested.Add(ToAbsoluteUri(uri).ToString());

        public void Land(string uri)
        {
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }

    [Fact]
    public void TwoUpdatesInOneEvent_KeepBothChanges()
    {
        var nav = new DeferredNavigationManager("http://spark/models?page=2");
        var query = UrlQuery.For(nav);

        query.Replace("tags", "vision");    // the tag filter
        query.Replace("page", null);         // the pager going back to page 1, in the same event

        Assert.Equal(["http://spark/models?page=2&tags=vision", "http://spark/models?tags=vision"], nav.Requested);
    }

    [Fact]
    public void OnceTheBrowserConfirms_UpdatesStartFromTheRealUriAgain()
    {
        var nav = new DeferredNavigationManager("http://spark/services");
        var query = UrlQuery.For(nav);

        query.Replace("tab", "discovered");
        nav.Land(nav.Requested[^1]);
        nav.Land("http://spark/services?tab=discovered&kind=user"); // e.g. Back, or a link: not one of ours
        query.Replace("discovered-page", 3);

        Assert.Equal("http://spark/services?tab=discovered&kind=user&discovered-page=3", nav.Requested[^1]);
    }

    [Fact]
    public void ANoOpUpdate_DoesNotNavigate()
    {
        var nav = new DeferredNavigationManager("http://spark/keys");

        UrlQuery.For(nav).Replace("page", null);

        Assert.Empty(nav.Requested);
    }
}
