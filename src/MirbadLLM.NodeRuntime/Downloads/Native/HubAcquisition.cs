using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>The official pinned snapshot and an optional validated, anonymous byte origin.</summary>
public sealed record HubAcquisition(HubSnapshot Snapshot, HubOrigin Origin, HubOrigin? Mirror)
{
    public static async Task<HubAcquisition> PrepareAsync(
        HubHttpClients clients, DownloadRequest request, IReadOnlyList<string> patterns, string? pinned, CancellationToken ct)
    {
        var origin = string.IsNullOrEmpty(request.Token) ? request.Hub ?? HubOrigin.Official : HubOrigin.Official;
        var authority = new HuggingFaceHubClient(clients.OfficialTransport(), clients.Official.BaseAddress ?? HubOrigin.OfficialEndpoint);
        var snapshot = await authority.GetSnapshotAsync(request.RepoId, pinned ?? request.Revision, request.Token, ct).ConfigureAwait(false);
        string? expected = pinned ?? (ContentDigest.IsHex(request.Revision, 40) ? request.Revision : null);
        if (expected is not null && !string.Equals(snapshot.CommitSha, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadRefusedException("The official Hub returned a different commit than the requested pin.");
        }

        if (origin.IsOfficial || !snapshot.IsPublicUngated)
        {
            return new HubAcquisition(snapshot, HubOrigin.Official, null);
        }

        HubOrigin? mirror = null;
        try
        {
            using var client = clients.OpenMirror(origin);
            var offered = await new HuggingFaceHubClient(client, mirror: true)
                .GetSnapshotAsync(request.RepoId, snapshot.CommitSha, null, ct).ConfigureAwait(false);
            var selected = snapshot.Files.Where(f => HuggingFacePatterns.MatchesAny(f.Path, patterns)).OrderBy(f => f.Path, StringComparer.Ordinal);
            var offeredSelected = offered.Files.Where(f => HuggingFacePatterns.MatchesAny(f.Path, patterns)).OrderBy(f => f.Path, StringComparer.Ordinal);
            if (offered.IsPublicUngated && offered.CommitSha == snapshot.CommitSha && selected.SequenceEqual(offeredSelected))
            {
                mirror = origin;
            }
        }
        catch (Exception ex) when (ex is HuggingFaceApiException or HttpRequestException or DownloadRefusedException or IOException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // An unavailable mirror leaves the independently pinned official snapshot usable.
        }

        return new HubAcquisition(snapshot, origin, mirror);
    }
}
