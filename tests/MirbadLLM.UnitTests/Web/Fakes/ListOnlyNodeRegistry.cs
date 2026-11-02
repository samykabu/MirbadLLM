using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>An <see cref="INodeRegistry"/> that only lists nodes (all the §5.6 default-placement rule reads).</summary>
public sealed class ListOnlyNodeRegistry(params NodeRecord[] nodes) : INodeRegistry
{
    /// <summary>A node record with only the required members set.</summary>
    public static NodeRecord Node(string id, bool revoked = false) => new()
    {
        Id = id,
        Uid = id + "-uid",
        Address = "https://127.0.0.1:9443",
        Revoked = revoked,
    };

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>(nodes);

    public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

    public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => throw new NotSupportedException();
}
