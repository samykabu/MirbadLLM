using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// DPAPI protection of the per-instance API key at rest in <c>processes.json</c> (design-p3p4 §A.8; MN-222):
/// <c>CryptProtectData</c> with <c>CRYPTPROTECT_LOCAL_MACHINE</c> (machine scope, so the virtual service account needs no
/// loaded profile and adoption after an agent restart works) and the service account's SID as the entropy, so another
/// account's blob, or this blob under another account, does not unprotect. P/Invoke of <c>crypt32</c> only: the
/// <c>System.Security.Cryptography.ProtectedData</c> package is not used (D107, no new package).
/// <para>The protected form is base64. Clear key bytes are zeroed after use. Windows only; the composition registers it on
/// a Windows process node (MN-232), tests elsewhere use the reversible fake.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class InstanceKeyProtector : IInstanceKeyProtector
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    private readonly byte[] _entropy;

    /// <summary>Protects under the account this process runs as (<c>NT SERVICE\mirbadllm-agent</c> in the service).</summary>
    public InstanceKeyProtector()
        : this(CurrentAccount())
    {
    }

    /// <summary>Protects with <paramref name="account"/>'s SID as the entropy.</summary>
    public InstanceKeyProtector(SecurityIdentifier account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _entropy = new byte[account.BinaryLength];
        account.GetBinaryForm(_entropy, 0);
    }

    /// <inheritdoc />
    public string Protect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] clear = Encoding.UTF8.GetBytes(key);
        try
        {
            return Convert.ToBase64String(Transform(clear, protect: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    /// <inheritdoc />
    public string Unprotect(string protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedKey);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The protected key is not base64.", ex);
        }

        if (blob.Length == 0)
        {
            throw new CryptographicException("The protected key is empty.");
        }

        byte[] clear = Transform(blob, protect: false);
        try
        {
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static SecurityIdentifier CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new CryptographicException("The process token has no user SID.");
    }

    private unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* inputPointer = input)
        fixed (byte* entropyPointer = _entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (nint)inputPointer };
            var entropy = new DataBlob { Size = _entropy.Length, Data = (nint)entropyPointer };
            var outBlob = default(DataBlob);
            bool ok = protect
                ? NativeMethods.CryptProtectData(&inBlob, 0, &entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, &outBlob)
                : NativeMethods.CryptUnprotectData(&inBlob, 0, &entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, &outBlob);
            if (!ok)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new CryptographicException(
                    $"{(protect ? "CryptProtectData" : "CryptUnprotectData")} failed: {Marshal.GetPInvokeErrorMessage(error)} (0x{error:X8}).");
            }

            try
            {
                var span = new Span<byte>((void*)outBlob.Data, outBlob.Size);
                byte[] output = span.ToArray();
                if (!protect)
                {
                    span.Clear(); // the clear key does not linger in the LocalAlloc'd buffer
                }

                return output;
            }
            finally
            {
                NativeMethods.LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CryptProtectData(
            DataBlob* dataIn, nint description, DataBlob* optionalEntropy, nint reserved, nint promptStruct, uint flags, DataBlob* dataOut);

        [LibraryImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CryptUnprotectData(
            DataBlob* dataIn, nint description, DataBlob* optionalEntropy, nint reserved, nint promptStruct, uint flags, DataBlob* dataOut);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial nint LocalFree(nint memory);
    }
}
