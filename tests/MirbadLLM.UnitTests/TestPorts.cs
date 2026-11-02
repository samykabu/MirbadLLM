using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace MirbadLLM.UnitTests;

/// <summary>
/// Ports for test servers that must know their port before they start (an agent address written into the master's
/// config, an enroll endpoint in a bootstrap bundle, an agent restarted on the same port). A server that can bind port 0
/// and report what it got (Kestrel <c>Listen(IPAddress.Loopback, 0)</c> plus <c>IServerAddressesFeature</c>) should do
/// that instead.
/// <para>A port from <c>bind(0)</c> that is closed and bound again later is racy: the OS hands it to the next
/// <c>bind(0)</c> or outgoing connection in this or another process, and the server then fails with "address already in
/// use". Ports from here instead come from <see cref="RangeStart"/>..<see cref="RangeEnd"/>, below every OS ephemeral
/// range (Windows 49152+, Linux 32768+), so the OS never hands them out on its own; each is probed free on every address
/// a test server binds (127.0.0.1, 0.0.0.0, [::]); and each stays reserved for the life of the process: in-process by a
/// set, across test processes on the machine (a second suite run in parallel) by an exclusively locked file per port.</para>
/// </summary>
internal static class TestPorts
{
    /// <summary>First port handed out.</summary>
    public const int RangeStart = 20_000;

    /// <summary>One past the last port handed out.</summary>
    public const int RangeEnd = 32_000;

    private static readonly Lock Gate = new();
    private static readonly HashSet<int> Reserved = [];
    private static readonly List<FileStream> Locks = [];
    private static readonly string LockDirectory = Path.Combine(Path.GetTempPath(), "mirbadllm-test-ports");

    // A random start per process, so two test processes do not walk the same ports.
    private static int _cursor = Random.Shared.Next(RangeStart, RangeEnd);

    /// <summary>A free port, reserved for this process.</summary>
    public static int Reserve() => ReserveBlock(1);

    /// <summary>
    /// True when <paramref name="port"/> was handed out by <see cref="Reserve"/>/<see cref="ReserveBlock"/> in this
    /// process. A test server must only bind ports for which this is true: consecutive reservations are adjacent (the
    /// cursor walks up), so a server that takes "my port + 1" takes the next test's port (MN-230).
    /// </summary>
    public static bool IsReserved(int port)
    {
        lock (Gate)
        {
            return Reserved.Contains(port);
        }
    }

    /// <summary>The first of <paramref name="count"/> consecutive free ports, all reserved for this process.</summary>
    public static int ReserveBlock(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, RangeEnd - RangeStart);
        lock (Gate)
        {
            for (int tried = 0; tried < RangeEnd - RangeStart; tried++)
            {
                int first = _cursor;
                _cursor = first + 1 >= RangeEnd ? RangeStart : first + 1;
                if (first + count > RangeEnd)
                {
                    continue;
                }

                if (TryReserveLocked(first, count))
                {
                    _cursor = first + count >= RangeEnd ? RangeStart : first + count;
                    return first;
                }
            }
        }

        throw new InvalidOperationException($"No block of {count} free ports between {RangeStart} and {RangeEnd}.");
    }

    /// <summary>
    /// True when <paramref name="exception"/> (or one it wraps) is a failed bind because the address is in use, as
    /// Kestrel (<see cref="AddressInUseException"/>), sockets and <c>WebApplicationFactory</c> report it.
    /// </summary>
    public static bool IsAddressInUse(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AddressInUseException
                || ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts a server again on a port it had before (a restart over the same port), retrying a few times while the bind
    /// fails because the port is still in use: the previous instance's listener can take a moment to release it. The
    /// port is this process's reservation, so nothing else is expected to hold it for long; any other failure, or the
    /// last one, is thrown.
    /// </summary>
    public static async Task<T> RestartOnSamePortAsync<T>(Func<Task<T>> start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await start();
            }
            catch (Exception ex) when (attempt < attempts && IsAddressInUse(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }

    private static bool TryReserveLocked(int first, int count)
    {
        var locks = new List<FileStream>(count);
        try
        {
            for (int port = first; port < first + count; port++)
            {
                if (Reserved.Contains(port) || !IsFree(port) || TryLock(port) is not { } fileLock)
                {
                    return false;
                }

                locks.Add(fileLock);
            }

            for (int port = first; port < first + count; port++)
            {
                Reserved.Add(port);
            }

            Locks.AddRange(locks);
            locks.Clear();
            return true;
        }
        finally
        {
            foreach (var fileLock in locks)
            {
                fileLock.Dispose();
            }
        }
    }

    /// <summary>The port is bindable on every address the test servers use (Kestrel's ListenAnyIP binds [::] dual-mode).</summary>
    private static bool IsFree(int port) =>
        CanBind(new IPEndPoint(IPAddress.Loopback, port))
        && CanBind(new IPEndPoint(IPAddress.Any, port))
        && (!Socket.OSSupportsIPv6 || CanBind(new IPEndPoint(IPAddress.IPv6Any, port)));

    private static bool CanBind(IPEndPoint endpoint)
    {
        try
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = true;
            }

            if (OperatingSystem.IsWindows())
            {
                // Fails when any other socket holds the port, on any address.
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(endpoint);
            socket.Listen(1);
            return true;
        }
        catch (SocketException)
        {
            // In use, or excluded by the OS (Windows reserves port ranges for Hyper-V and WSL).
            return false;
        }
    }

    /// <summary>An exclusive lock on the port's file (held for the process's life), or null when another process holds it.</summary>
    private static FileStream? TryLock(int port)
    {
        try
        {
            Directory.CreateDirectory(LockDirectory);
            return new FileStream(
                Path.Combine(LockDirectory, port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
