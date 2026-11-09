using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Storage;

/// <summary>A pull the approved-image guardrail accepted (see <see cref="AgentImageService.Authorize"/>).</summary>
/// <param name="Repository">Repository to pull.</param>
/// <param name="TagOrDigest">What to pull: the approved digest, or the tag on a node that allows tag pulls.</param>
/// <param name="Reference">The reference the result reports (<c>repo:tag</c> or <c>repo@digest</c>).</param>
/// <param name="Digest">The approved digest; null for a tag pull.</param>
public sealed record AuthorizedPull(string Repository, string TagOrDigest, string Reference, string? Digest)
{
    /// <summary>The login for a private registry; null for a public image.</summary>
    public RegistryCredentials? Registry { get; init; }
}

/// <summary>
/// Images on the node (design §4.3 <c>GET /images</c>, <c>POST /images/pull</c>, §10.3 images pinned by digest): lists
/// local images, and runs pulls as operations with progress (stage <c>pulling</c>, <see cref="OperationEvent"/>s with
/// <see cref="OperationProgress"/>, durable progress at most every <see cref="AgentStorageOptions.ProgressRecordInterval"/>)
/// ending with the pulled <see cref="ImageInfo"/> as the result. Only approved <c>repository@sha256:…</c> pairs are
/// pulled; a pull without a digest only where <see cref="AgentStorageOptions.AllowTagPulls"/> is set.
/// </summary>
public sealed class AgentImageService(
    IContainerImageEngine engine,
    StorageOperations operations,
    IOptions<AgentStorageOptions> options,
    TimeProvider time,
    ILogger<AgentImageService> logger) : IAsyncDisposable
{
    private readonly AgentStorageOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, RunningPull> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>Local images.</summary>
    public async Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            return await engine.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDockerFailure(ex))
        {
            throw StorageErrors.DockerUnavailable(ex);
        }
    }

    /// <summary>
    /// The approved-image guardrail (design §10.3). Throws 422 <c>spec_rejected</c> for an unparseable reference, a
    /// malformed digest, a digest in the reference that differs from <see cref="ImagePullRequest.Digest"/>, a digest not
    /// approved for the repository, or a tag without a digest where tag pulls are not allowed.
    /// </summary>
    public AuthorizedPull Authorize(string? image, string? digest)
    {
        var reference = ImageReference.Parse(image);
        if (reference.Repository.Length == 0)
        {
            throw StorageErrors.SpecRejected("The image reference is empty or malformed.");
        }

        if (reference.Digest is not null && digest is not null && !string.Equals(reference.Digest, digest, StringComparison.Ordinal))
        {
            throw StorageErrors.SpecRejected("The digest in the image reference differs from the requested digest.");
        }

        string? pinned = digest ?? reference.Digest;
        if (pinned is null)
        {
            if (!_options.AllowTagPulls)
            {
                throw StorageErrors.SpecRejected(
                    $"'{reference.Repository}' was requested without a digest; this node only pulls approved repository@sha256 images.");
            }

            string tag = reference.Tag ?? "latest";
            return new AuthorizedPull(reference.Repository, tag, $"{reference.Repository}:{tag}", null);
        }

        if (!ImageReference.IsValidDigest(pinned))
        {
            throw StorageErrors.SpecRejected("The digest is not a sha256 digest (sha256: followed by 64 lowercase hex digits).");
        }

        string wanted = ImageReference.Pinned(reference.Repository, pinned);
        bool approved = _options.ApprovedImages.Any(entry => string.Equals(entry.Trim(), wanted, StringComparison.Ordinal));
        if (!approved)
        {
            throw StorageErrors.SpecRejected($"'{wanted}' is not in this node's approved images.");
        }

        return new AuthorizedPull(reference.Repository, pinned, reference.Tag is null ? wanted : $"{reference.Repository}:{reference.Tag}", pinned);
    }

    /// <summary>
    /// Executes an accepted pull operation: the guardrail (failure → the operation fails before its first stage, i.e. a
    /// refusal), then the pull runs in the background. Returns the status to answer 202 with.
    /// </summary>
    public async Task<OperationStatus> StartAsync(ImagePullRequest request, OperationStatus accepted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(accepted);
        var pull = Authorize(request.Image, request.Digest) with { Registry = request.Registry };
        var status = await operations.StageAsync(accepted.Id, new OperationStageUpdate
        {
            Stage = OperationStages.Pulling,
            State = OperationState.Running,
            Progress = new OperationProgress { Message = $"pulling {pull.Reference}" },
        }, ct).ConfigureAwait(false);
        Run(accepted.Id, pull);
        StorageLog.ImagePullStarted(logger, accepted.Id, pull.Reference);
        return status;
    }

    /// <summary>
    /// Resumes a pull after an agent restart (design §4.4 item 5): a pull is a desired-state action, so it is simply run
    /// again from the stored (secret-free) payload; the approval is re-checked against the current configuration.
    /// </summary>
    public async Task ResumeAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ImagePullRequest? request = null;
        try
        {
            request = operation.Payload?.Deserialize<ImagePullRequest>(AgentJson.Options);
        }
        catch (JsonException)
        {
            // Handled below.
        }

        if (request is null)
        {
            await operations.CompleteAsync(operation.Status.Id, OperationState.Failed,
                StorageErrors.Error(ErrorCodes.InternalError, "The image pull could not be resumed: its stored request is unreadable."), ct: ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Registry is not null)
        {
            // Its password is not stored, so the request read back can't log in.
            await operations.CompleteAsync(operation.Status.Id, OperationState.Failed,
                StorageErrors.Error(ErrorCodes.InternalError, "The image pull could not be resumed: its registry login is not stored. Start it again."), ct: ct)
                .ConfigureAwait(false);
            return;
        }

        AuthorizedPull pull;
        try
        {
            pull = Authorize(request.Image, request.Digest);
        }
        catch (AgentException ex)
        {
            await operations.CompleteAsync(operation.Status.Id, OperationState.Failed, ex.Error, ct: ct).ConfigureAwait(false);
            return;
        }

        await operations.StageAsync(operation.Status.Id, new OperationStageUpdate
        {
            Stage = OperationStages.Pulling,
            State = OperationState.Running,
            Progress = new OperationProgress { Message = $"resuming pull of {pull.Reference} after an agent restart" },
        }, ct).ConfigureAwait(false);
        Run(operation.Status.Id, pull);
    }

    /// <summary>
    /// Cancels a running pull (design §4.3 <c>/operations/{id}/cancel</c>): the operation ends Cancelled. False when
    /// <paramref name="operationId"/> is not a pull running in this process.
    /// </summary>
    public async Task<bool> CancelAsync(string operationId, CancellationToken ct = default)
    {
        if (!_running.TryGetValue(operationId, out var running))
        {
            return false;
        }

        running.CancelRequested = true;
        await running.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The pull task observed the cancellation.
        }

        return true;
    }

    /// <summary>True while a pull runs in this process (tests and the cancel route).</summary>
    public bool IsRunning(string operationId) => _running.ContainsKey(operationId);

    /// <summary>Waits for a running pull to end (tests).</summary>
    public Task WhenFinishedAsync(string operationId) =>
        _running.TryGetValue(operationId, out var running) ? running.Task : Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var running in _running.Values)
        {
            try
            {
                await running.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: the operation stays non-terminal and is resumed at the next start.
            }
        }

        _shutdown.Dispose();
    }

    private void Run(string operationId, AuthorizedPull pull)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var running = new RunningPull(cts);
        _running[operationId] = running;
        running.Task = Task.Run(() => PullAsync(operationId, pull, running), CancellationToken.None);
    }

    private async Task PullAsync(string operationId, AuthorizedPull pull, RunningPull running)
    {
        var ct = running.Cancellation.Token;
        DateTimeOffset lastRecorded = time.GetUtcNow();
        try
        {
            await engine.PullAsync(pull.Repository, pull.TagOrDigest, pull.Registry, progress =>
            {
                var update = new OperationProgress
                {
                    BytesDone = progress.BytesDone,
                    BytesTotal = progress.BytesTotal,
                    Percent = progress.BytesTotal > 0 ? Math.Round(100.0 * progress.BytesDone / progress.BytesTotal, 1) : null,
                    Message = string.Create(CultureInfo.InvariantCulture,
                        $"{progress.Message ?? "pulling"} (layers {progress.LayersDone}/{progress.LayersTotal})"),
                };
                var now = time.GetUtcNow();
                if (now - lastRecorded >= _options.ProgressRecordInterval)
                {
                    lastRecorded = now;
                    running.PendingRecord = operations.StageAsync(operationId, new OperationStageUpdate { Progress = update }, CancellationToken.None);
                }
                else if (operations.Log.Find(operationId) is { } current)
                {
                    operations.Publish(current with { Progress = update });
                }
            }, ct).ConfigureAwait(false);

            if (running.PendingRecord is { } pending)
            {
                await pending.ConfigureAwait(false);
            }

            string inspectRef = pull.Digest is null ? pull.Reference : ImageReference.Pinned(pull.Repository, pull.Digest);
            var info = await engine.InspectAsync(inspectRef, CancellationToken.None).ConfigureAwait(false);
            if (info is null)
            {
                await FailAsync(operationId, StorageErrors.Error(ErrorCodes.InternalError, $"Docker finished the pull but '{inspectRef}' is not present.")).ConfigureAwait(false);
                return;
            }

            if (pull.Digest is not null && info.Digest is not null && !string.Equals(info.Digest, pull.Digest, StringComparison.Ordinal))
            {
                await FailAsync(operationId, StorageErrors.Error(ErrorCodes.SpecRejected,
                    $"The pulled image has digest {info.Digest}, not the approved {pull.Digest}.", new SpecRejectedDetails("digest mismatch"))).ConfigureAwait(false);
                return;
            }

            await operations.CompleteAsync(operationId, OperationState.Succeeded,
                result: AgentJson.ToElement(info with { Ref = pull.Reference })).ConfigureAwait(false);
            StorageLog.ImagePullFinished(logger, operationId, pull.Reference);
        }
        catch (OperationCanceledException) when (running.CancelRequested)
        {
            await operations.CompleteAsync(operationId, OperationState.Cancelled).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Agent shutdown: left non-terminal and resumed by the recovery handler at the next start.
        }
        catch (ImagePullFailedException ex)
        {
            await FailAsync(operationId, StorageErrors.Error(ErrorCodes.InternalError, $"Docker could not pull {pull.Reference}: {ex.Message}")).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDockerFailure(ex))
        {
            await FailAsync(operationId, StorageErrors.DockerUnavailable(ex).Error).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(operationId, out _);
            running.Cancellation.Dispose();
        }
    }

    private async Task FailAsync(string operationId, AgentError error)
    {
        StorageLog.ImagePullFailed(logger, operationId, error.Code);
        await operations.CompleteAsync(operationId, OperationState.Failed, error).ConfigureAwait(false);
    }

    internal static bool IsDockerFailure(Exception ex) =>
        ex is DockerApiException or HttpRequestException or SocketException or IOException ||
        (ex is InvalidOperationException && ex.Message.Contains("Docker", StringComparison.OrdinalIgnoreCase));

    private sealed class RunningPull(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        public Task<OperationStatus>? PendingRecord { get; set; }

        public bool CancelRequested { get; set; }
    }
}
