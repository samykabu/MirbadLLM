using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// Race-free event triggering for pages that re-render on their own. Root cause of the intermittent
/// <c>ModelTuneApplyTests</c> failures found by MN-090 (<c>UnknownEventHandlerIdException: There is no event handler
/// with ID 'N'</c>): the Tune page's lambda handlers get new event-handler ids on every render, and the page renders
/// by itself — when an async handler such as <c>SelectSearchAsync</c> finishes its preview refresh (the second
/// render of an event), and every second from its poll timer. A test that waits for the <em>first</em> render of an
/// event and then <c>Find(...).Change(...)</c>s the next element can pick up an id the late render has just retired.
/// Under full-suite CPU load the window is wide enough to hit.
/// <para>The helpers close both windows: they find and trigger on the renderer's dispatcher, <b>wait for the
/// handler to finish</b> (so the event's own late render has happened before the test continues), and if a poll
/// render still retired the id first, find the element again and retry. Retrying is safe: an unknown id is
/// refused before any handler runs, so the event was never delivered.</para>
/// </summary>
internal static class BunitDispatch
{
    private const int Attempts = 5;
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Raises <c>onchange</c> with <paramref name="value"/> on <paramref name="selector"/> and waits for the handler.</summary>
    public static void ChangeAt<TComponent>(this IRenderedComponent<TComponent> cut, string selector, bool value)
        where TComponent : IComponent =>
        Wait(Trigger(cut, selector, e => e.ChangeAsync(new ChangeEventArgs { Value = value })));

    /// <summary>Raises <c>onchange</c> with a string <paramref name="value"/> (selects, text inputs) and waits for the handler.</summary>
    public static void ChangeAt<TComponent>(this IRenderedComponent<TComponent> cut, string selector, string value)
        where TComponent : IComponent =>
        Wait(Trigger(cut, selector, e => e.ChangeAsync(new ChangeEventArgs { Value = value })));

    /// <summary>Clicks <paramref name="selector"/> and waits for the handler.</summary>
    public static void ClickAt<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent =>
        Wait(Trigger(cut, selector, e => e.ClickAsync(new())));

    /// <summary>
    /// Clicks <paramref name="selector"/> and returns the handler's task <b>without</b> waiting for it, for tests that
    /// assert on the page while the handler is still running.
    /// </summary>
    public static Task ClickAtAsync<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent =>
        Trigger(cut, selector, e => e.ClickAsync(new()));

    private static Task Trigger<TComponent>(IRenderedComponent<TComponent> cut, string selector, Func<IElement, Task> raise)
        where TComponent : IComponent
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Task handler = Task.CompletedTask;
                cut.InvokeAsync(() => { handler = raise(cut.Find(selector)); }).GetAwaiter().GetResult();
                if (attempt < Attempts && handler.IsFaulted && handler.Exception?.InnerException is UnknownEventHandlerIdException)
                {
                    continue;
                }

                return handler;
            }
            catch (UnknownEventHandlerIdException) when (attempt < Attempts)
            {
                // A render retired the id between the DOM snapshot and the dispatch; nothing was delivered.
            }
        }
    }

    private static void Wait(Task handler)
    {
        // WaitAsync + GetResult rethrows the handler's own exception (not an AggregateException) and bounds the wait.
        handler.WaitAsync(HandlerTimeout).GetAwaiter().GetResult();
    }
}
