using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// Loopback port checks for supervised processes (design-p3p4 §A.1.4; MN-207, D121 / spike S1 C11): on a desktop PC other
/// programs hold ports (the owner's own llama-server listens on 8000), so before a create the driver skips every port that
/// any process listens on, on <b>any</b> address (<see cref="IPGlobalProperties.GetActiveTcpListeners"/>), and then
/// test-binds <c>127.0.0.1:{port}</c> with exclusive address use. It only ever looks at ports; it never touches the
/// process holding one.
/// </summary>
public static class ProcessPorts
{
    /// <summary>True when <paramref name="port"/> is in use (a listener on any address, or the loopback test-bind fails).</summary>
    public static bool IsTaken(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
            {
                return true;
            }
        }
        catch (NetworkInformationException)
        {
            // The listener table is unavailable: the test-bind below still decides.
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (OperatingSystem.IsWindows())
            {
                // Fails when any other socket holds the port, on any address.
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// <paramref name="preferred"/> when it is free, else the next port above it that is free and not in
    /// <paramref name="avoid"/> (ports of the driver's other instances). Throws <see cref="InvalidOperationException"/> when
    /// none is left below 65536.
    /// </summary>
    public static int Choose(int preferred, IReadOnlySet<int> avoid, Func<int, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(avoid);
        isTaken ??= IsTaken;
        for (int port = preferred; port <= 65535; port++)
        {
            if (!avoid.Contains(port) && !isTaken(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No free loopback port at or above {preferred}.");
    }
}
