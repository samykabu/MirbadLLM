using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Updates;

/// <summary>
/// The update helper's checks in C# (docs/multinode/agent-updates.md U4/U5), for Windows, where the helper is the agent's
/// own executable run as the SYSTEM service <c>mirbadllm-agent-updater</c> (Linux and macOS run
/// <c>agent-update-helper.sh</c>, which does the same). Nothing in the spool is trusted as is: order, signature and bundle
/// are copied into a private folder, the signature is checked against each certificate of <c>{trust}/ca.pem</c> (the
/// master CA an admin-run installer saved), the order against <c>{trust}/node-uid</c>, <c>{trust}/rid</c> and its expiry,
/// the bundle against the order's size and SHA-256; only then is the bundle's own installer run.
/// </summary>
public sealed class UpdateHelperCore(string spool, string trust, TimeProvider time)
{
    /// <summary>Runs one update when the spool holds a signed order; writes <c>{spool}/result</c> and consumes the order.</summary>
    /// <param name="install">Runs the bundle's installer: (unpacked bundle folder, bundle file, SHA-256) → exit code and output.</param>
    public async Task<HelperResult?> RunAsync(Func<string, string, string, Task<(int ExitCode, string Output)>> install, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        string signatureFile = Path.Combine(spool, AgentUpdateOptions.SignatureFile);
        if (!File.Exists(signatureFile))
        {
            return null;
        }

        string operation = ReadOperation();
        string work = Path.Combine(Path.GetTempPath(), "mirbadllm-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (code, version, message, output) = await CheckAndInstallAsync(work, install, ct).ConfigureAwait(false);
            var result = new HelperResult(operation, code, version, (message + "\n" + output).Trim());
            await WriteResultAsync(result, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            TryDelete(signatureFile);
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temp folder is cleaned by Windows later.
            }
        }
    }

    private async Task<(int Code, string? Version, string Message, string Output)> CheckAndInstallAsync(
        string work, Func<string, string, string, Task<(int, string)>> install, CancellationToken ct)
    {
        string orderText;
        string signature;
        try
        {
            orderText = await File.ReadAllTextAsync(Path.Combine(spool, AgentUpdateOptions.OrderFile), ct).ConfigureAwait(false);
            signature = (await File.ReadAllTextAsync(Path.Combine(spool, AgentUpdateOptions.SignatureFile), ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (2, null, "The order could not be read.", ex.Message);
        }

        if (!Verified(orderText, signature, out string? trustProblem))
        {
            return (3, null, trustProblem ?? "The order is not signed by this node's master: refused.", "");
        }

        if (AgentUpdateOrder.Parse(orderText) is not { } order)
        {
            return (3, null, "The order has an unknown format.", "");
        }

        if (!string.Equals(order.NodeUid, ReadTrust("node-uid"), StringComparison.Ordinal))
        {
            return (3, order.Version, "The order is for another node.", "");
        }

        if (!string.Equals(order.Rid, ReadTrust("rid"), StringComparison.Ordinal))
        {
            return (3, order.Version, $"The order is for {order.Rid}, not this node's bundle.", "");
        }

        if (order.ExpiresAt <= time.GetUtcNow())
        {
            return (3, order.Version, "The order expired; start the update again from the master.", "");
        }

        if (order.Version.Length is 0 or > 64 || !order.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return (3, order.Version, "The order names an invalid version.", "");
        }

        string source = Path.Combine(spool, AgentUpdateOptions.BundleFile(order.Rid));
        string bundle = Path.Combine(work, AgentUpdateOptions.BundleFile(order.Rid));
        try
        {
            if (new FileInfo(source).LinkTarget is not null)
            {
                return (4, order.Version, "The bundle is a link: refused.", "");
            }

            File.Copy(source, bundle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (4, order.Version, "The bundle is missing.", ex.Message);
        }

        if (new FileInfo(bundle).Length != order.Size)
        {
            return (4, order.Version, "The bundle's size does not match the order.", "");
        }

        await using (var stream = File.OpenRead(bundle))
        {
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)), order.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (4, order.Version, "The bundle's SHA-256 does not match the order.", "");
            }
        }

        string unpacked = Path.Combine(work, "x");
        try
        {
            ZipFile.ExtractToDirectory(bundle, unpacked);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return (4, order.Version, "The bundle could not be unpacked.", ex.Message);
        }

        string folder = Path.Combine(unpacked, $"mirbadllm-agent-{order.Version}-{order.Rid}");
        if (!Directory.Exists(folder))
        {
            // A bundle built before the rename.
            folder = Path.Combine(unpacked, $"{LegacyNames.BundleFolderPrefix}{order.Version}-{order.Rid}");
        }

        if (!Directory.Exists(folder))
        {
            return (4, order.Version, $"The bundle has no mirbadllm-agent-{order.Version}-{order.Rid} folder.", "");
        }

        var (exit, output) = await install(folder, bundle, order.Sha256).ConfigureAwait(false);
        return exit == 0
            ? (0, order.Version, $"Installed {order.Version}.", output)
            : (exit, order.Version, "The installer failed (it switches back to the previous agent when the new one does not come up).", output);
    }

    /// <summary>True when one of the trusted CA certificates verifies the order's signature.</summary>
    public bool Verified(string orderText, string signatureBase64, out string? problem)
    {
        problem = null;
        string caFile = Path.Combine(trust, "ca.pem");
        if (!File.Exists(caFile))
        {
            problem = $"No trusted CA at {caFile}: run the installer's upgrade as administrator.";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            problem = "The order's signature is not base64.";
            return false;
        }

        byte[] data = Encoding.UTF8.GetBytes(orderText);
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPemFile(caFile);
        try
        {
            foreach (var certificate in certificates)
            {
                using var key = certificate.GetECDsaPublicKey();
                if (key is not null && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }

        return false;
    }

    private string ReadOperation()
    {
        try
        {
            string text = File.ReadAllText(Path.Combine(spool, AgentUpdateOptions.OperationFile)).Trim();
            return new string([.. text.Take(200).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private string? ReadTrust(string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(trust, name)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteResultAsync(HelperResult result, CancellationToken ct)
    {
        string text = $"operation={result.OperationId}\nexit={result.ExitCode}\nversion={result.Version}\nlog:\n{result.Log}\n";
        string part = Path.Combine(spool, AgentUpdateOptions.ResultFile + ".part");
        await File.WriteAllTextAsync(part, text, ct).ConfigureAwait(false);
        File.Move(part, Path.Combine(spool, AgentUpdateOptions.ResultFile), overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next order overwrites it.
        }
    }
}
