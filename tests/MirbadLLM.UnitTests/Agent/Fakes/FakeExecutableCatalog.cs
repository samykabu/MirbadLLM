using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.UnitTests.Agent.Fakes;

/// <summary>
/// In-memory <see cref="IExecutableCatalog"/> (MN-211): versions are added with their verification result; <see cref="Resolve"/>
/// throws the catalogue's 422 codes (<c>executable_missing</c>, <c>executable_unverified</c>) as the real one (MN-222)
/// does. Each version gets a folder under the test's runtimes root (with an empty <c>llama-server.exe</c>).
/// </summary>
internal sealed class FakeExecutableCatalog(string runtimesRoot) : IExecutableCatalog
{
    private readonly List<(ExecutableVersionState State, ResolvedExecutable Resolved)> _versions = [];

    public int ResolveCalls { get; private set; }

    public ResolvedExecutable Add(string version, bool verified = true, bool isDefault = true, string name = ExecutableNames.LlamaServer)
    {
        string folder = Path.Combine(runtimesRoot, "llama.cpp", version);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "llama-server.exe");
        File.WriteAllBytes(path, []);
        var resolved = new ResolvedExecutable(name, version, path, folder, "13.4");
        if (isDefault)
        {
            for (int i = 0; i < _versions.Count; i++)
            {
                _versions[i] = (_versions[i].State with { IsDefault = false }, _versions[i].Resolved);
            }
        }

        _versions.Add((new ExecutableVersionState(name, version, "13.4", verified, isDefault, verified ? null : "llama.dll: hash mismatch"), resolved));
        return resolved;
    }

    public IReadOnlyList<ExecutableVersionState> List() => [.. _versions.Select(v => v.State)];

    public ResolvedExecutable Resolve(string name, string? version)
    {
        ResolveCalls++;
        var match = _versions.FirstOrDefault(v =>
            string.Equals(v.State.Name, name, StringComparison.Ordinal) &&
            (version is null ? v.State.IsDefault : string.Equals(v.State.Version, version, StringComparison.Ordinal)));
        if (match.Resolved is null)
        {
            throw new AgentException(422, new AgentError
            {
                Code = ErrorCodes.ExecutableMissing,
                Message = $"Executable '{name}' {version ?? "(default)"} is not installed on this node.",
            });
        }

        if (!match.State.Verified)
        {
            throw new AgentException(422, new AgentError
            {
                Code = ErrorCodes.ExecutableUnverified,
                Message = $"Executable '{name}' {match.State.Version} failed verification: {match.State.Problem}.",
            });
        }

        return match.Resolved;
    }

    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}
