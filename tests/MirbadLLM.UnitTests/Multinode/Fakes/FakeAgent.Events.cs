using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// The event bus and <c>/events</c> (design §3.4, §4.7): every state change gets the next per-boot <c>seq</c> under the
/// state lock and goes into a ring (capacity / max age); <c>/status</c> is built under the same lock, so its <c>seq</c> is
/// an exact watermark. A cursor of the current boot still in the ring replays from <c>seq + 1</c>; anything else gets one
/// <c>reset</c> and the stream closes. Each subscriber has a bounded queue; on overflow it gets <c>overflow</c> after the
/// events already queued and the stream closes. Heartbeats carry the current seq and are not stored.
/// </summary>
internal sealed partial class FakeAgent
{
    private readonly LinkedList<AgentEvent> _ring = new();
    private readonly List<Subscriber> _subscribers = [];
    private long _seq;

    /// <summary>
    /// Live <c>/events</c> subscribers registered on the bus. A subscription the master has sent but the agent has not
    /// registered yet gets the events published meanwhile as a replay from the ring, not through its bounded queue.
    /// </summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Stamps (boot, seq, time), stores and fans out a state-change event. Caller holds the state lock.</summary>
    private void PublishLocked(AgentEvent evt)
    {
        var now = Now;
        var stamped = evt with { BootId = _boot.Id, Seq = ++_seq, At = now };
        _ring.AddLast(stamped);
        while (_ring.Count > _options.EventRingCapacity || (_ring.First is { } first && now - first.Value.At > _options.EventRingMaxAge))
        {
            _ring.RemoveFirst();
        }

        foreach (var subscriber in _subscribers)
        {
            subscriber.EnqueueLocked(stamped, _options.SubscriberQueueCapacity);
        }
    }

    private void AbortSubscribersLocked()
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.AbortLocked();
        }

        _subscribers.Clear();
    }

    private async Task<FakeReply> EventsAsync(FakeCall call)
    {
        var query = (EventsQuery)call.Body!;
        var http = call.Http;
        var subscriber = new Subscriber();
        AgentEvent? reset = null;
        lock (_gate)
        {
            if (query.BootId is not null && query.BootId != _boot.Id)
            {
                reset = new ResetEvent { Reason = ResetEvent.BootChanged, BootId = _boot.Id, Seq = _seq, At = Now };
            }
            else if (query.AfterSeq is { } after)
            {
                long oldest = _ring.First?.Value.Seq ?? _seq + 1;
                if (after > _seq || after < oldest - 1)
                {
                    reset = new ResetEvent { Reason = ResetEvent.Evicted, BootId = _boot.Id, Seq = _seq, At = Now };
                }
                else
                {
                    foreach (var evt in _ring.Where(e => e.Seq > after))
                    {
                        subscriber.EnqueueLocked(evt, int.MaxValue);
                    }
                }
            }

            if (reset is null)
            {
                _subscribers.Add(subscriber);
            }
        }

        http.Response.StatusCode = 200;
        http.Response.ContentType = AgentJson.EventStreamContentType;
        http.Response.Headers.CacheControl = "no-cache";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ct = http.RequestAborted;
        try
        {
            await http.Response.StartAsync(ct);
            await http.Response.Body.FlushAsync(ct);
            if (reset is not null)
            {
                await WriteEventAsync(http, reset, ct);
                return FakeReply.StreamDone;
            }

            while (!ct.IsCancellationRequested)
            {
                AgentEvent? next = null;
                bool overflow = false;
                bool aborted = false;
                lock (_gate)
                {
                    if (subscriber.Queue.Count > 0)
                    {
                        next = subscriber.Queue.Dequeue();
                    }
                    else if (subscriber.Overflowed)
                    {
                        overflow = true;
                        next = new OverflowEvent { BootId = _boot.Id, Seq = _seq, At = Now };
                    }
                    else if (subscriber.Aborted)
                    {
                        aborted = true;
                    }
                }

                if (aborted)
                {
                    http.Abort();
                    break;
                }

                if (next is not null)
                {
                    await WriteEventAsync(http, next, ct);
                    if (overflow)
                    {
                        break;
                    }

                    continue;
                }

                if (!await subscriber.Signal.WaitAsync(_options.HeartbeatInterval, ct))
                {
                    HeartbeatEvent heartbeat;
                    lock (_gate)
                    {
                        heartbeat = new HeartbeatEvent { BootId = _boot.Id, Seq = _seq, At = Now };
                    }

                    await WriteEventAsync(http, heartbeat, ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The master went away.
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
            }

            subscriber.Signal.Dispose();
        }

        return FakeReply.StreamDone;
    }

    private static async Task WriteEventAsync(HttpContext http, AgentEvent evt, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(evt, AgentJson.Options);
        byte[] frame = Encoding.UTF8.GetBytes($"event: {evt.EventType}\ndata: {json}\n\n");
        await http.Response.Body.WriteAsync(frame, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private sealed class Subscriber
    {
        public Queue<AgentEvent> Queue { get; } = new();

        public SemaphoreSlim Signal { get; } = new(0);

        public bool Overflowed { get; private set; }

        public bool Aborted { get; private set; }

        public void EnqueueLocked(AgentEvent evt, int capacity)
        {
            if (Overflowed || Aborted)
            {
                return;
            }

            if (Queue.Count >= capacity)
            {
                Overflowed = true;
            }
            else
            {
                Queue.Enqueue(evt);
            }

            Release();
        }

        public void AbortLocked()
        {
            Aborted = true;
            Queue.Clear();
            Release();
        }

        private void Release()
        {
            try
            {
                Signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // The stream already ended.
            }
        }
    }
}
