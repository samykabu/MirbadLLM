using MirbadLLM.Core;
using MirbadLLM.Orchestration;

namespace MirbadLLM.UnitTests.Downloads.Fakes;

/// <summary>Hand-written fake <see cref="IHuggingFaceRepoInspector"/>: returns a seeded <see cref="RepoInspection"/> per repo id, or throws a seeded error.</summary>
public sealed class FakeHuggingFaceRepoInspector : IHuggingFaceRepoInspector
{
    private readonly Dictionary<string, RepoInspection> _inspections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HuggingFaceApiException> _errors = new(StringComparer.OrdinalIgnoreCase);

    public List<(string RepoId, string? Revision, string? Token)> InspectCalls { get; } = [];

    public void Seed(string repoId, RepoInspection inspection) => _inspections[repoId] = inspection;

    public void SeedError(string repoId, HuggingFaceApiException exception) => _errors[repoId] = exception;

    public Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
    {
        InspectCalls.Add((repoId, revision, token));

        if (_errors.TryGetValue(repoId, out var error))
        {
            throw error;
        }

        return _inspections.TryGetValue(repoId, out var inspection)
            ? Task.FromResult(inspection)
            : Task.FromResult(new RepoInspection(repoId, revision, RepoFormat.Other, [], [], [], []));
    }
}
