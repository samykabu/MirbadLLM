using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace MirbadLLM.Web.Components.Ui;

/// <summary>
/// Writes UI state into the URL query (Tabs' ?tab=, TagFilter's ?tags=, Pager's ?page=&amp;size=) by replacing the
/// current history entry. On an interactive circuit <see cref="NavigationManager.Uri"/> only changes once the
/// browser confirms a navigation, so two components updating the query in the same event (a tag toggle and the
/// pager going back to page 1) would each start from the old URI and the later one would drop the earlier one's
/// key. This builds every update on top of the navigations still in flight instead, one instance per
/// NavigationManager (that is, per circuit).
/// </summary>
public sealed class UrlQuery
{
    private static readonly ConditionalWeakTable<NavigationManager, UrlQuery> Instances = [];

    private readonly NavigationManager _nav;
    private readonly List<string> _inFlight = [];
    private readonly Lock _gate = new();

    private UrlQuery(NavigationManager nav)
    {
        _nav = nav;
        _nav.LocationChanged += OnLocationChanged;
    }

    /// <summary>The writer for this circuit's <paramref name="nav"/>.</summary>
    public static UrlQuery For(NavigationManager nav) => Instances.GetValue(nav, n => new UrlQuery(n));

    /// <summary>Sets (or, with a null value, removes) query parameters, keeping every other one.</summary>
    public void Replace(IReadOnlyDictionary<string, object?> parameters)
    {
        string uri;
        lock (_gate)
        {
            string current = _inFlight.Count > 0 ? _inFlight[^1] : _nav.Uri;
            uri = _nav.GetUriWithQueryParameters(current, parameters);
            if (string.Equals(uri, current, StringComparison.Ordinal))
            {
                return;
            }

            _inFlight.Add(uri);
        }

        _nav.NavigateTo(uri, replace: true);
    }

    /// <summary>Sets (or, with null, removes) one query parameter.</summary>
    public void Replace(string name, object? value) => Replace(new Dictionary<string, object?> { [name] = value });

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        lock (_gate)
        {
            int index = _inFlight.FindIndex(u => string.Equals(u, e.Location, StringComparison.Ordinal));
            if (index >= 0)
            {
                _inFlight.RemoveRange(0, index + 1); // this one and every earlier one have landed
            }
            else
            {
                _inFlight.Clear(); // somewhere else entirely (a link, Back): start from the real URI again
            }
        }
    }
}
