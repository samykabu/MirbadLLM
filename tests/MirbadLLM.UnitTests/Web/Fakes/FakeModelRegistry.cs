using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>
/// Hand-written in-memory <see cref="IModelRegistry"/> that mirrors the validation rules the real
/// design (docs/architecture.md) assigns to the Orchestration workstream's registry: Id regex and
/// ServedName uniqueness enforced with <see cref="ArgumentException"/>.
/// </summary>
public sealed class FakeModelRegistry : IModelRegistry
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ModelDefinition> _models = new(StringComparer.Ordinal);

    public FakeModelRegistry()
    {
    }

    public FakeModelRegistry(IEnumerable<ModelDefinition> seed)
    {
        foreach (var model in seed)
        {
            _models[model.Id] = model;
        }
    }

    public Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelDefinition>>(_models.Values.ToList());

    public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_models.GetValueOrDefault(id));

    public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default)
    {
        if (!IdPattern.IsMatch(model.Id))
        {
            throw new ArgumentException($"Id '{model.Id}' must match ^[a-z0-9][a-z0-9-]{{0,39}}$.", nameof(model));
        }

        // Like the real registry: tags normalized to slugs, ArgumentException for an invalid one or too many.
        model = model with { Tags = ModelTags.NormalizeAll(model.Tags) };

        if (_models.Values.Any(m => m.Id != model.Id && string.Equals(m.ServedName, model.ServedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Served name '{model.ServedName}' is already in use.", nameof(model));
        }

        _models[model.Id] = model;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(_models.Remove(id));
}
