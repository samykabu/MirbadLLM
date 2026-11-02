using System.Net;
using System.Net.Sockets;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// The network between the master and the agent: a TCP relay on loopback that the master's node record points at
/// (the agent's mTLS endpoint is end to end, the relay only moves bytes). Faults:
/// <list type="bullet">
/// <item><see cref="Partition"/>: every link stops moving bytes in both directions (new connections are accepted but stall
/// in the TLS handshake), as when packets are dropped; <see cref="Heal"/> resumes them, or resets them as connections
/// that timed out during the partition.</item>
/// <item><see cref="ResetAll"/>: every open connection is reset now (in-flight requests and responses are lost).</item>
/// <item><see cref="TargetPort"/>: where the agent listens now (an agent restart may move it); while it is 0 the agent is
/// down and connections are refused.</item>
/// </list>
/// </summary>
internal sealed class FaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Link> _links = [];
    private readonly Task _accepting;
    private TaskCompletionSource _flowing = Open();

    public FaultProxy(int targetPort)
    {
        TargetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptLoopAsync();
    }

    /// <summary>The port the master dials.</summary>
    public int Port { get; }

    /// <summary>The agent's current port; 0 = agent down (connections are refused).</summary>
    public int TargetPort { get; set; }

    public bool Partitioned
    {
        get
        {
            lock (_gate)
            {
                return !_flowing.Task.IsCompleted;
            }
        }
    }

    public int OpenLinks
    {
        get
        {
            lock (_gate)
            {
                return _links.Count;
            }
        }
    }

    public void Partition()
    {
        lock (_gate)
        {
            if (_flowing.Task.IsCompleted)
            {
                _flowing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Ends a partition; <paramref name="reset"/> resets the connections that were stalled instead of resuming them.</summary>
    public void Heal(bool reset)
    {
        if (reset)
        {
            ResetAll();
        }

        lock (_gate)
        {
            _flowing.TrySetResult();
        }
    }

    /// <summary>Resets every open connection (RST): whatever was in flight is lost.</summary>
    public void ResetAll()
    {
        List<Link> links;
        lock (_gate)
        {
            links = [.. _links];
            _links.Clear();
        }

        foreach (var link in links)
        {
            link.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Heal(reset: true);
        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }

    private static TaskCompletionSource Open()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    private Task WhenFlowing()
    {
        lock (_gate)
        {
            return _flowing.Task;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ConnectAsync(client));
        }
    }

    private async Task ConnectAsync(Socket client)
    {
        client.NoDelay = true;
        int target = TargetPort;
        var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (target == 0)
            {
                throw new SocketException((int)SocketError.ConnectionRefused);
            }

            await server.ConnectAsync(new IPEndPoint(IPAddress.Loopback, target), _stop.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent is down: the master sees its connection refused (reset).
            Abort(client);
            server.Dispose();
            return;
        }

        var link = new Link(client, server);
        lock (_gate)
        {
            _links.Add(link);
        }

        await Task.WhenAll(PumpAsync(link, client, server), PumpAsync(link, server, client));
        link.Dispose();
        lock (_gate)
        {
            _links.Remove(link);
        }
    }

    private async Task PumpAsync(Link link, Socket from, Socket to)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, link.Token);
                // A partition holds bytes that were already read until it heals (or the link is reset).
                await WhenFlowing().WaitAsync(link.Token);
                if (read == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, link.Token);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            link.Abort();
        }
    }

    private static void Abort(Socket socket)
    {
        try
        {
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        socket.Dispose();
    }

    private sealed class Link(Socket client, Socket server) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _aborted;

        public CancellationToken Token => _cts.Token;

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
            {
                return;
            }

            _cts.Cancel();
            FaultProxy.Abort(client);
            FaultProxy.Abort(server);
        }

        /// <summary>After both pumps ended.</summary>
        public void Dispose()
        {
            Abort();
            _cts.Dispose();
        }
    }
}
