using System.Security.Cryptography;
using System.Text;

namespace MirbadLLM.Core;

/// <summary>Constant-time string comparison, shared by the admin password check and the API-key checks so
/// timing does not leak how many leading characters matched.</summary>
public static class ConstantTimeCompare
{
    public static bool Equals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
