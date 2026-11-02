using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Pki;

/// <summary>One agent-certificate rotation as the agent persists it (design §10.2). Public material only.</summary>
public sealed record AgentRotationEntry
{
    /// <summary>Rotation id chosen by the master.</summary>
    public required string RotationId { get; init; }

    /// <summary>Agent-side stage.</summary>
    public required PkiRotationStage Stage { get; init; }

    /// <summary>CSR of the pending key, returned again on a repeated start.</summary>
    public required string CsrPem { get; init; }

    /// <summary>SPKI pin of the pending key (the new certificate's key).</summary>
    public required string PendingSpkiSha256 { get; init; }

    /// <summary>SPKI pin of the certificate the rotation replaces.</summary>
    public string? PreviousSpkiSha256 { get; init; }

    /// <summary>Expiry of the installed certificate.</summary>
    public DateTimeOffset? CertNotAfter { get; init; }

    /// <summary>
    /// For <see cref="PkiRotationStage.Committed"/>: true once the pending key and certificate were promoted to
    /// <c>agent.key</c>/<c>agent.crt</c> and the pending files deleted. A committed, unfinalized rotation is finished at the
    /// next commit retry or agent start.
    /// </summary>
    public bool Finalized { get; init; }

    /// <summary>When the rotation started on the agent.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the stage last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary><c>{state}/pki-rotation.json</c>: the open rotation and the most recent finished ones.</summary>
public sealed record AgentRotationDocument
{
    /// <summary>Schema version of this file.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Finished rotations kept for <c>GET /pki/rotations/{id}</c>.</summary>
    public const int MaxHistory = 16;

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// The open rotation: <see cref="PkiRotationStage.Pending"/>, <see cref="PkiRotationStage.CertInstalled"/>, or
    /// <see cref="PkiRotationStage.Committed"/> while its promotion is not finalized. Null when none.
    /// </summary>
    public AgentRotationEntry? Active { get; init; }

    /// <summary>Finished rotations (committed and finalized, or aborted), newest first.</summary>
    public IReadOnlyList<AgentRotationEntry> History { get; init; } = [];

    /// <summary>The rotation with this id (open or finished), or null.</summary>
    public AgentRotationEntry? Find(string rotationId) =>
        Active is { } active && string.Equals(active.RotationId, rotationId, StringComparison.Ordinal)
            ? active
            : History.FirstOrDefault(e => string.Equals(e.RotationId, rotationId, StringComparison.Ordinal));

    /// <summary>A copy with <paramref name="finished"/> moved to the history (and no open rotation).</summary>
    public AgentRotationDocument Finish(AgentRotationEntry finished) => this with
    {
        Active = null,
        History = [finished, .. History.Where(e => !string.Equals(e.RotationId, finished.RotationId, StringComparison.Ordinal)).Take(MaxHistory - 1)],
    };
}

/// <summary>
/// The agent's rotation files in its state directory (design §10.2): <c>pki-rotation.json</c> (stages, public),
/// <c>agent.pending.key</c> (0600) and <c>agent.pending.crt</c>. Every write is atomic (<see cref="PkiFiles"/>: temp file,
/// flush, rename) and followed by a directory fsync, so the order of the writes is the order a crash can observe:
/// the pending key is on disk before the stage that names it, and "committed" is on disk before the old key is replaced.
/// Not thread-safe: <see cref="AgentPkiService"/> serializes every change. Key material is never logged.
/// </summary>
public sealed class AgentRotationStore
{
    /// <summary>The rotation stages (public).</summary>
    public const string DocumentFileName = "pki-rotation.json";

    /// <summary>The pending private key (PKCS#8 PEM, 0600).</summary>
    public const string PendingKeyFileName = "agent.pending.key";

    /// <summary>The pending certificate (PEM).</summary>
    public const string PendingCertificateFileName = "agent.pending.crt";

    private readonly string _directory;

    /// <summary>Opens the store over a state directory and loads <see cref="Document"/>.</summary>
    public AgentRotationStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _directory = stateDirectory;
        Document = Load();
    }

    /// <summary>The persisted rotation document.</summary>
    public AgentRotationDocument Document { get; private set; }

    /// <summary>Test hook: called at named points between writes; a throwing hook simulates a crash there.</summary>
    internal Action<string>? FaultInjection { get; set; }

    /// <summary>True when the pending key file exists.</summary>
    public bool HasPendingKey => File.Exists(PathOf(PendingKeyFileName));

    /// <summary>True when the pending certificate file exists.</summary>
    public bool HasPendingCertificate => File.Exists(PathOf(PendingCertificateFileName));

    /// <summary>Persists <paramref name="document"/> (atomic, directory flushed) and makes it current.</summary>
    public void Save(AgentRotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string json = JsonSerializer.Serialize(document, AgentJson.Options);
        PkiFiles.WritePublic(PathOf(DocumentFileName), json);
        DirectorySync.Flush(_directory);
        Document = document;
    }

    /// <summary>Writes the pending private key (mode 0600). The caller must not keep the PEM string longer than needed.</summary>
    public void WritePendingKey(string privateKeyPem)
    {
        PkiFiles.WriteSecret(PathOf(PendingKeyFileName), privateKeyPem);
        DirectorySync.Flush(_directory);
    }

    /// <summary>Writes the pending certificate.</summary>
    public void WritePendingCertificate(string certificatePem)
    {
        PkiFiles.WritePublic(PathOf(PendingCertificateFileName), certificatePem);
        DirectorySync.Flush(_directory);
    }

    /// <summary>The pending private key PEM, or null.</summary>
    public string? ReadPendingKey() => PkiFiles.ReadIfExists(PathOf(PendingKeyFileName));

    /// <summary>The pending certificate PEM, or null.</summary>
    public string? ReadPendingCertificate() => PkiFiles.ReadIfExists(PathOf(PendingCertificateFileName));

    /// <summary>
    /// Promotes the pending key and certificate to <c>agent.key</c>/<c>agent.crt</c> (design §10.2 step 4: this replaces,
    /// and so deletes, the old key). Only called after "committed" is on disk. Idempotent: a crash between the two writes
    /// leaves the pending files in place and the next call writes both again.
    /// </summary>
    public void PromotePending()
    {
        string key = ReadPendingKey() ?? throw new IOException("The pending key is missing; the rotation cannot be promoted.");
        string certificate = ReadPendingCertificate() ?? throw new IOException("The pending certificate is missing; the rotation cannot be promoted.");
        PkiFiles.WriteSecret(PathOf(FileAgentCredentials.KeyFileName), key);
        FaultInjection?.Invoke("promote:key-written");
        PkiFiles.WritePublic(PathOf(FileAgentCredentials.CertificateFileName), certificate);
        DirectorySync.Flush(_directory);
    }

    /// <summary>Deletes the pending certificate, then the pending key (missing files are fine).</summary>
    public void DeletePendingFiles()
    {
        bool deleted = Delete(PendingCertificateFileName);
        deleted |= Delete(PendingKeyFileName);
        if (deleted)
        {
            DirectorySync.Flush(_directory);
        }
    }

    /// <summary>Deletes the pending certificate only (the key stays for a new install of the same rotation).</summary>
    public void DeletePendingCertificate()
    {
        if (Delete(PendingCertificateFileName))
        {
            DirectorySync.Flush(_directory);
        }
    }

    /// <summary>Writes the trust anchors file (<c>ca.crt</c>, one or two CA certificates).</summary>
    public void WriteTrustAnchors(IEnumerable<string> certificatePems)
    {
        ArgumentNullException.ThrowIfNull(certificatePems);
        PkiFiles.WritePublic(PathOf(FileAgentCredentials.CaFileName), string.Concat(certificatePems.Select(p => p.TrimEnd() + "\n")));
        DirectorySync.Flush(_directory);
    }

    private AgentRotationDocument Load()
    {
        string? json = PkiFiles.ReadIfExists(PathOf(DocumentFileName));
        if (json is null)
        {
            return new AgentRotationDocument();
        }

        try
        {
            AgentRotationDocument document = JsonSerializer.Deserialize<AgentRotationDocument>(json, AgentJson.Options)
                ?? throw new InvalidDataException("The rotation file is empty.");
            if (document.SchemaVersion != AgentRotationDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported rotation file schema version {document.SchemaVersion}.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            // Writes are atomic, so a corrupt file is not a torn write: stop rather than guess which key is current.
            throw new InvalidDataException($"'{PathOf(DocumentFileName)}' is not a valid rotation file; fix or remove it before starting the agent.", ex);
        }
    }

    private bool Delete(string fileName)
    {
        string path = PathOf(fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathOf(string fileName) => Path.Combine(_directory, fileName);
}
