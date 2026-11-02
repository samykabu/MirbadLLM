namespace MirbadLLM.Core;

/// <summary>
/// A saved credential for an outside service (a Hugging Face access token today), kept once on the master and picked by
/// name wherever one is needed: a download, a move, a restore. The record never carries the value, only
/// <see cref="Hint"/>, the last four characters, so two tokens can be told apart.
/// </summary>
public sealed record SavedToken(string Id, string Name, string Hint, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// A saved token with its value, for the encrypted secrets section of a backup only. A class, not a record, so the value
/// never appears in a generated <c>ToString()</c>.
/// </summary>
public sealed class SavedTokenSecret
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The token itself. Secret.</summary>
    public required string Value { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public override string ToString() => $"SavedTokenSecret {{ Id = {Id}, Name = {Name}, Value = *** }}";
}

/// <summary>
/// The master's saved tokens. Values are encrypted at rest and only leave the store through <see cref="GetValueAsync"/>
/// (server side, to hand to a download) and <see cref="ExportAsync"/> (into an encrypted backup). Names are unique,
/// ignoring case.
/// </summary>
public interface ITokenStore
{
    /// <summary>Every saved token, by name.</summary>
    Task<IReadOnlyList<SavedToken>> ListAsync(CancellationToken ct = default);

    /// <summary>Saves a new token. Throws <see cref="ArgumentException"/> for an empty name or value, or a name in use.</summary>
    Task<SavedToken> AddAsync(string name, string value, CancellationToken ct = default);

    /// <summary>
    /// Renames a token and, when <paramref name="value"/> is not blank, replaces its value. Null when <paramref name="id"/>
    /// is unknown; throws <see cref="ArgumentException"/> for an empty name or a name in use.
    /// </summary>
    Task<SavedToken?> UpdateAsync(string id, string name, string? value, CancellationToken ct = default);

    /// <summary>Removes a token. False when <paramref name="id"/> is unknown.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>The token's value, or null when <paramref name="id"/> is unknown.</summary>
    Task<string?> GetValueAsync(string id, CancellationToken ct = default);

    /// <summary>Every token with its value, for a backup's encrypted secrets.</summary>
    Task<IReadOnlyList<SavedTokenSecret>> ExportAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds tokens from a backup. A token whose id is already here, or whose name is taken, is skipped, so an import never
    /// overwrites a token the owner changed since.
    /// </summary>
    Task<SavedTokenImportResult> ImportAsync(IReadOnlyList<SavedTokenSecret> tokens, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="ITokenStore.ImportAsync"/>.</summary>
public sealed record SavedTokenImportResult(int Added, int Skipped);
