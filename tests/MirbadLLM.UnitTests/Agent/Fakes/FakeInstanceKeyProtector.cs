using System.Security.Cryptography;
using System.Text;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.UnitTests.Agent.Fakes;

/// <summary>
/// Reversible <see cref="IInstanceKeyProtector"/> for tests (MN-211): the protected form never contains the clear key
/// (base64 of the reversed bytes behind a marker), and a value without the marker cannot be unprotected, like DPAPI data
/// from another machine or account.
/// </summary>
internal sealed class FakeInstanceKeyProtector : IInstanceKeyProtector
{
    public const string Marker = "fake-dpapi:";

    public int Protected { get; private set; }

    /// <summary>Thrown by the next <see cref="Protect"/> (DPAPI failing), then cleared (MN-219).</summary>
    public Exception? FailNextProtect { get; set; }

    public string Protect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (FailNextProtect is { } failure)
        {
            FailNextProtect = null;
            throw failure;
        }

        Protected++;
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        Array.Reverse(bytes);
        return Marker + Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        if (!protectedKey.StartsWith(Marker, StringComparison.Ordinal))
        {
            throw new CryptographicException("The data was not protected by this protector.");
        }

        byte[] bytes = Convert.FromBase64String(protectedKey[Marker.Length..]);
        Array.Reverse(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
