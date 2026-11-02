using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Updates;

/// <summary>
/// Where the agent and the node's root update helper meet (docs/multinode/agent-updates.md U4/U5). The agent writes the
/// bundle, the order and its signature into <see cref="Spool"/> (the signature last: it is what the helper watches); the
/// helper writes <see cref="ResultFile"/>. <see cref="HelperMarker"/> is a root-owned file the installer creates with the
/// helper: without it the node does not offer <see cref="AgentFeatures.AgentUpdate"/>.
/// </summary>
public sealed class AgentUpdateOptions
{
    /// <summary>The spool folder (agent-owned, inside the state directory).</summary>
    public required string Spool { get; init; }

    /// <summary>The root-owned marker that says the helper is installed.</summary>
    public required string HelperMarker { get; init; }

    /// <summary>How long a dispatched update may wait for the helper's result or the agent's restart.</summary>
    public TimeSpan ResultWait { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Windows: start the helper service after the hand-over (off in tests).</summary>
    public bool StartWindowsHelper { get; init; } = true;

    /// <summary>The largest bundle the agent downloads.</summary>
    public long MaxBundleBytes { get; init; } = 1L << 30;

    public const string OrderFile = "order";
    public const string SignatureFile = "order.sig";
    public const string OperationFile = "operation";
    public const string ResultFile = "result";

    /// <summary>The bundle's file in the spool: <c>bundle.zip</c> on Windows, else <c>bundle.tar.gz</c>.</summary>
    public static string BundleFile(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) ? "bundle.zip" : "bundle.tar.gz";

    /// <summary>The marker each OS's installer writes (Linux <c>/etc</c>, macOS the agent's root-owned config folder, Windows Program Files).</summary>
    public static string DefaultHelperMarker()
    {
        string marker = HelperMarkerOf("mirbadllm-agent");
        string legacy = HelperMarkerOf(LegacyNames.AgentName);
        return !File.Exists(marker) && File.Exists(legacy) ? legacy : marker; // an install made before the rename
    }

    private static string HelperMarkerOf(string name) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name, "update", "helper")
            : OperatingSystem.IsMacOS()
                ? $"/Library/Application Support/{name}/update/helper"
                : $"/etc/{name}/update/helper";

    public bool HelperInstalled => File.Exists(HelperMarker);
}

/// <summary>
/// The agent's part of a pushed update (docs/multinode/agent-updates.md U3–U7): accept the order like every mutation
/// (fencing, durable log), check that it is for this node and this bundle, download the bundle from the master that sent
/// it and verify its SHA-256 and size, then hand bundle, order and signature to the helper through the spool. The helper
/// verifies the signature itself (the agent's check here only saves a useless download); the operation completes when
/// the agent runs the new build or the helper reports a failure (<see cref="AgentUpdateWatcher"/>).
/// </summary>
public sealed partial class AgentUpdateService(
    AgentUpdateOptions options,
    IOperationLog log,
    IFencingState fencing,
    IAgentCredentials credentials,
    AgentBootInfo boot,
    HttpClient http,
    ILogger<AgentUpdateService> logger)
{
    /// <summary>This agent's bundle: <c>linux-x64</c>, <c>linux-arm64</c>, <c>win-x64</c> or <c>osx-arm64</c>.</summary>
    public static string ThisRid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public AgentUpdateOptions Options => options;

    /// <summary>The agent's informational version, e.g. <c>1.0.0+826bc75fc222</c>.</summary>
    public string RunningVersion => boot.AgentVersion;

    /// <summary>The agent's own build revision (after '+').</summary>
    public string? RunningRevision => RevisionOf(boot.AgentVersion);

    public static string? RevisionOf(string? version)
    {
        int plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return plus < 0 ? null : version![(plus + 1)..];
    }

    /// <summary>Validates and accepts an update order; the download and the hand-over run in the background (202).</summary>
    public async Task<HostCallResult> SubmitAsync(AgentUpdateRequest request, string? masterHeader, JsonElement body, IPAddress? master, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.HelperInstalled)
        {
            return HostCallResult.Fail(ErrorCodes.NotSupported, "This node has no update helper; update its agent with the installer.");
        }

        if (!MasterTriple.TryParse(masterHeader, out var triple))
        {
            return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"Missing or malformed {Protocol.MasterHeader} header.");
        }

        if (Validate(request, master) is { } invalid)
        {
            return HostCallResult.Fail(ErrorCodes.InvalidRequest, invalid);
        }

        var check = await fencing.CheckAsync(triple, ct).ConfigureAwait(false);
        if (check.Decision == FencingDecision.StaleMaster)
        {
            return HostCallResult.Fail(ErrorCodes.StaleMaster, "A master with a higher epoch already drives this node.");
        }

        if (check.Decision == FencingDecision.CompetingMaster)
        {
            return HostCallResult.Fail(ErrorCodes.CompetingMaster, "Another master instance already drives this node at this epoch.");
        }

        if (log.ListActive().FirstOrDefault(o => o.Kind == OperationKinds.AgentUpdate && o.Id != request.OperationId) is { } busy)
        {
            return HostCallResult.Fail(ErrorCodes.InvalidRequest, $"Update {busy.Id} is still in progress on this node.");
        }

        OperationAcceptance acceptance;
        try
        {
            acceptance = await log.AcceptAsync(new OperationSubmission
            {
                OperationId = request.OperationId,
                Kind = OperationKinds.AgentUpdate,
                Target = request.Version,
                IssuedAt = request.IssuedAt,
                ExpiresAt = request.ExpiresAt,
                Master = triple,
                Payload = body,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStateUnavailable(logger, request.OperationId, ex);
            return HostCallResult.Fail(ErrorCodes.StateUnavailable, "The operation log could not record the update.");
        }

        switch (acceptance.Outcome)
        {
            case OperationAcceptOutcome.Duplicate when acceptance.Status is { } stored:
                return new HostCallResult(stored.State is OperationState.Accepted or OperationState.Running ? 202 : 200, stored);
            case OperationAcceptOutcome.Conflict:
                return HostCallResult.Fail(ErrorCodes.OperationIdConflict, $"Operation id {request.OperationId} was already used for another update.");
            case OperationAcceptOutcome.Expired:
                return HostCallResult.Fail(ErrorCodes.OperationExpired, $"Operation {request.OperationId} expired at {request.ExpiresAt:O}.");
            case OperationAcceptOutcome.IssuedInFuture:
                return HostCallResult.Fail(ErrorCodes.ClockSkew, "The operation's issuedAt is more than 5 minutes ahead of the agent clock.");
            case OperationAcceptOutcome.Accepted when acceptance.Status is { } accepted:
                _ = Task.Run(() => HandOverAsync(request, master!, triple), CancellationToken.None); // outlives the request
                return new HostCallResult(202, accepted);
            default:
                return HostCallResult.Fail(ErrorCodes.InternalError, "The operation log returned no status.");
        }
    }

    /// <summary>Why the order cannot be for this node, or null.</summary>
    public string? Validate(AgentUpdateRequest request, IPAddress? master)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (master is null)
        {
            return "The master's address is unknown.";
        }

        if (AgentUpdateOrder.Parse(request.Order) is not { } order)
        {
            return "The update order is not in the expected format.";
        }

        if (!string.Equals(order.NodeUid, credentials.NodeUid, StringComparison.Ordinal))
        {
            return "The update order is for another node.";
        }

        if (!string.Equals(order.Rid, ThisRid, StringComparison.Ordinal) || !string.Equals(request.Rid, ThisRid, StringComparison.Ordinal))
        {
            return $"The update order is for {order.Rid}; this agent is {ThisRid}.";
        }

        if (!string.Equals(order.Version, request.Version, StringComparison.Ordinal) || !string.Equals(order.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase)
            || order.Size != request.Size)
        {
            return "The update order does not match the request.";
        }

        if (request.Size <= 0 || request.Size > options.MaxBundleBytes || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit))
        {
            return "The bundle's size or SHA-256 is not valid.";
        }

        if (!request.BundlePath.StartsWith("/agent/bundles/", StringComparison.Ordinal) || request.BundlePath.Contains("..", StringComparison.Ordinal)
            || request.BundlePort is <= 0 or > 65535)
        {
            return "The bundle's location is not the master's bundle route.";
        }

        if (request.Signature.Length is 0 or > 512)
        {
            return "The update order carries no signature.";
        }

        return null;
    }

    /// <summary>Downloads and verifies the bundle, then writes the spool. Completes the operation as Failed on any problem.</summary>
    internal async Task HandOverAsync(AgentUpdateRequest request, IPAddress master, MasterTriple triple)
    {
        string id = request.OperationId;
        try
        {
            Directory.CreateDirectory(options.Spool);
            foreach (string stale in Directory.EnumerateFiles(options.Spool))
            {
                File.Delete(stale);
            }

            await log.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Downloading, State = OperationState.Running }).ConfigureAwait(false);
            string host = master.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{master}]" : master.ToString();
            var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{request.BundlePort}{request.BundlePath}"));
            string bundle = Path.Combine(options.Spool, AgentUpdateOptions.BundleFile(request.Rid));
            string? problem = await DownloadAsync(url, bundle, request.Size, request.Sha256).ConfigureAwait(false);
            if (problem is not null)
            {
                await FailAsync(id, problem).ConfigureAwait(false);
                return;
            }

            if (!fencing.IsCurrent(triple))
            {
                await FailAsync(id, "A newer master took over this node; the update was not handed to the helper.", ErrorCodes.Superseded).ConfigureAwait(false);
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(options.Spool, AgentUpdateOptions.OperationFile), id + "\n").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(options.Spool, AgentUpdateOptions.OrderFile), request.Order).ConfigureAwait(false);
            await log.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Dispatched, State = OperationState.Running }).ConfigureAwait(false);
            // Last: the helper starts on this file.
            string signature = Path.Combine(options.Spool, AgentUpdateOptions.SignatureFile);
            await File.WriteAllTextAsync(signature + ".part", request.Signature.Trim() + "\n").ConfigureAwait(false);
            File.Move(signature + ".part", signature, overwrite: true);
            LogHandedOver(logger, id, request.Version);
            // Linux and macOS start their helper on the file itself; on Windows the agent starts the helper service.
            if (OperatingSystem.IsWindows() && options.StartWindowsHelper)
            {
                WindowsUpdateHelper.TryStart(logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            await FailAsync(id, $"The update could not be prepared: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<string?> DownloadAsync(Uri url, string target, long size, string sha256)
    {
        string part = target + ".part";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return $"The master answered {(int)response.StatusCode} for {url.AbsolutePath}.";
        }

        await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > size)
                {
                    return "The bundle is larger than the order says.";
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            if (total != size)
            {
                return $"The bundle has {total} bytes; the order says {size}.";
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "The bundle's SHA-256 does not match the order.";
            }
        }

        File.Move(part, target, overwrite: true);
        return null;
    }

    internal async Task FailAsync(string id, string message, string code = ErrorCodes.InternalError)
    {
        try
        {
            await log.CompleteAsync(id, OperationState.Failed, AgentErrorResults.Create(code, message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStateUnavailable(logger, id, ex);
        }

        LogFailed(logger, id, message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent update {OperationId}: bundle {Version} verified and handed to the update helper.")]
    private static partial void LogHandedOver(ILogger logger, string operationId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent update {OperationId} failed: {Message}")]
    private static partial void LogFailed(ILogger logger, string operationId, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent update {OperationId}: the operation log is unavailable.")]
    private static partial void LogStateUnavailable(ILogger logger, string operationId, Exception exception);
}
