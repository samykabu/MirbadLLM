using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MirbadLLM.Web;

/// <summary>One merged pull request in the release notes (docs/multinode/agent-updates.md, U1/U2).</summary>
public sealed record ReleaseEntry
{
    public int Number { get; init; }

    public string Title { get; init; } = "";

    public DateTimeOffset MergedAt { get; init; }

    /// <summary>The merge commit on main: the revision of the build that first carried this change.</summary>
    public string Commit { get; init; } = "";

    public string? Url { get; init; }

    public IReadOnlyList<string> Added { get; init; } = [];

    public IReadOnlyList<string> Fixed { get; init; } = [];

    public IReadOnlyList<string> Changed { get; init; } = [];
}

/// <summary>What changed between two builds: the entries after <see cref="From"/> up to <see cref="To"/>, newest first.</summary>
/// <param name="From">The older build's revision; null when unknown (then <see cref="Entries"/> are the latest ones).</param>
/// <param name="To">The newer build's revision.</param>
/// <param name="Entries">Newest first.</param>
/// <param name="Truncated">True when <see cref="From"/> is older than the notes go back (or unknown): more changed than listed.</param>
/// <param name="Available">False when this build has no notes (built without a token, or locally).</param>
public sealed record ReleaseNotesRange(string? From, string? To, IReadOnlyList<ReleaseEntry> Entries, bool Truncated, bool Available);

/// <summary>
/// The release notes baked into this build (<c>release-notes.json</c> next to the app, written by the deploy job from the
/// merged PRs; <c>deploy/release-notes.py</c>). Entries are oldest first and keyed by merge commit, so "what changed from
/// build A to build B" is every entry after A's commit up to B's. Read once; a missing or unreadable file means "no notes".
/// </summary>
public sealed class ReleaseNotes
{
    public const string FileName = "release-notes.json";

    /// <summary>How many entries a range with an unknown start lists.</summary>
    public const int DefaultRecent = 15;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ReleaseNotes(IReadOnlyList<ReleaseEntry> entries, bool available = true)
    {
        Entries = [.. entries.OrderBy(e => e.MergedAt)];
        Available = available;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<ReleaseEntry> Entries { get; }

    public bool Available { get; }

    /// <summary>Loads <paramref name="path"/>; a missing or broken file gives empty, unavailable notes.</summary>
    public static ReleaseNotes Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ReleaseNotes([], available: false);
            }

            var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json);
            var entries = (file?.Entries ?? []).Where(e => e.Number > 0 && e.Commit.Length >= 7).ToList();
            return new ReleaseNotes(entries, available: file?.Error is null && entries.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseNotes([], available: false);
        }
    }

    /// <summary>
    /// The entries after <paramref name="fromRevision"/> up to <paramref name="toRevision"/> (default: the newest), newest
    /// first. An unknown or absent start lists the <see cref="DefaultRecent"/> latest up to the end, marked truncated;
    /// the same start and end is an empty range.
    /// </summary>
    public ReleaseNotesRange Between(string? fromRevision, string? toRevision = null)
    {
        int end = toRevision is null ? Entries.Count - 1 : IndexOf(toRevision);
        if (end < 0)
        {
            // A build newer than its own notes (built locally, or the notes failed): everything we know.
            end = Entries.Count - 1;
        }

        int start = fromRevision is null ? -2 : IndexOf(fromRevision);
        bool truncated = start == -1 || start == -2;
        IEnumerable<ReleaseEntry> picked = truncated
            ? Entries.Take(end + 1).TakeLast(DefaultRecent)
            : Entries.Skip(start + 1).Take(Math.Max(0, end - start));
        return new ReleaseNotesRange(fromRevision, toRevision, [.. picked.Reverse()], truncated && end >= 0, Available);
    }

    /// <summary>The entry a revision's build was merged as, or null.</summary>
    public ReleaseEntry? Find(string? revision) => IndexOf(revision) is var i and >= 0 ? Entries[i] : null;

    private int IndexOf(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length < 7)
        {
            return -1;
        }

        string clean = revision.Split('-')[0];
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i].Commit.StartsWith(clean, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class FileDto
    {
        public string? Error { get; set; }

        [JsonPropertyName("entries")]
        public List<ReleaseEntry>? Entries { get; set; }
    }
}

/// <summary><c>GET /api/releases?from=&amp;to=</c>: the release notes between two builds (admin, like every <c>/api</c> route).</summary>
public static class ReleaseNotesEndpoints
{
    public static IEndpointRouteBuilder MapReleaseNotes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/releases", (string? from, string? to, ReleaseNotes notes) =>
        {
            var range = notes.Between(from, to);
            return Results.Ok(new
            {
                controller = ProductVersion.Controller,
                current = ProductVersion.RevisionOf(ProductVersion.Controller),
                range.Available,
                range.Truncated,
                range.Entries,
            });
        });
        return endpoints;
    }
}
