using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// Settings of the development join-token helper (MN-042), section <see cref="SectionName"/>. The join-token API and the
/// approval dialog are P2 (MN-102), so the two-agent dev setup has no other way to add a second node:
/// <code>
/// "MirbadLLM": { "DevJoinTokens": { "Nodes": [ "dev-b" ], "OutputDirectory": "artifacts/dev-multinode/join", "AutoApprove": true } }
/// </code>
/// Refused outside the Development environment (the master does not start).
/// </summary>
public sealed class DevJoinTokenOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:DevJoinTokens";

    /// <summary>Node ids to mint a join token for while they are not enrolled. Empty = the helper is off.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration.")]
    public List<string>? Nodes { get; set; }

    /// <summary>Where <c>{nodeId}.join.json</c> is written (owner-only on Unix). Required when <see cref="Nodes"/> is set.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Approve these nodes as soon as they enroll (the P2 approval dialog's job).</summary>
    public bool AutoApprove { get; set; } = true;

    /// <summary>Token lifetime (10 min – 24 h, as the registry enforces).</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often pending nodes are looked for when <see cref="AutoApprove"/> is on.</summary>
    public TimeSpan ApproveInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>What the helper writes for one node: everything <c>mirbadllm-agent join</c> needs.</summary>
/// <param name="NodeId">The node id the token is bound to.</param>
/// <param name="EnrollUrl">The enroll listener (<see cref="EnrollmentOptions.BootstrapEnrollEndpoint"/>).</param>
/// <param name="CaHash">The CA pin as <c>sha256:…</c>.</param>
/// <param name="Token">The one-time join token.</param>
/// <param name="ExpiresAt">When the token stops working.</param>
public sealed record DevJoinFile(string NodeId, Uri EnrollUrl, string CaHash, string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>File name of a node's join file.</summary>
    public static string FileName(string nodeId) => nodeId + ".join.json";

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"DevJoinFile {{ NodeId = {NodeId}, EnrollUrl = {EnrollUrl}, CaHash = {CaHash}, Token = {Protocol.RedactedValue} }}";
}

/// <summary>Registration and guard of the development join-token helper.</summary>
public static class DevJoinTokens
{
    /// <summary>
    /// Binds <see cref="DevJoinTokenOptions"/> and adds <see cref="DevJoinTokenService"/>, which does nothing unless
    /// <see cref="DevJoinTokenOptions.Nodes"/> is set. The settings are read when the host starts (so test hosts' and
    /// command-line overrides apply), and <see cref="Validate"/> runs then: outside Development the master does not start.
    /// </summary>
    public static IServiceCollection AddMirbadLLMDevJoinTokens(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<DevJoinTokenOptions>().Bind(configuration.GetSection(DevJoinTokenOptions.SectionName));
        services.AddHostedService<DevJoinTokenService>();
        return services;
    }

    /// <summary>True when the helper is configured (<see cref="DevJoinTokenOptions.Nodes"/> is not empty).</summary>
    public static bool IsEnabled(DevJoinTokenOptions options) => options?.Nodes is { Count: > 0 };

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the configured helper must not run: outside the Development
    /// environment, for an invalid node id or the bootstrap node, or without an output directory.
    /// </summary>
    public static void Validate(DevJoinTokenOptions options, IHostEnvironment environment, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsEnabled(options))
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{DevJoinTokenOptions.SectionName} mints join tokens and approves nodes without an operator; it is a development-only " +
                $"setting and the environment is '{environment.EnvironmentName}'. Remove it.");
        }

        foreach (string node in options.Nodes!)
        {
            if (!Protocol.IsValidNodeId(node))
            {
                throw new InvalidOperationException($"{DevJoinTokenOptions.SectionName}:Nodes: '{node}' is not a node id.");
            }

            if (string.Equals(node, bootstrapNodeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{DevJoinTokenOptions.SectionName}:Nodes: '{node}' is the bootstrap node; it enrolls with the bootstrap bundle.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new InvalidOperationException($"{DevJoinTokenOptions.SectionName}:OutputDirectory is required.");
        }
    }
}

/// <summary>
/// Development join-token helper (MN-042, W4 review MN-096): at start, mints a join token
/// (<see cref="INodeRegistry.CreateJoinTokenAsync"/>, purpose <see cref="JoinTokenPurpose.Join"/>) for every configured
/// node that is not enrolled and writes <see cref="DevJoinFile"/> to the output directory; then, with
/// <see cref="DevJoinTokenOptions.AutoApprove"/>, approves those nodes once they enrolled (pending pin promoted, as the
/// P2 approval will). Runs in the master process, so <c>nodes.json</c> has one writer. Inert unless configured; refused outside Development.
/// </summary>
public sealed partial class DevJoinTokenService(
    IServiceProvider services,
    IOptions<DevJoinTokenOptions> settings,
    IHostEnvironment environment,
    IOptions<RegistryMigrationOptions> migration,
    TimeProvider time,
    ILogger<DevJoinTokenService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DevJoinTokenOptions _options = settings.Value;

    private INodeRegistry Registry => services.GetRequiredService<INodeRegistry>();

    /// <summary>Refuses to start (and so stops the master) when the helper is configured where it must not run.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!DevJoinTokens.IsEnabled(_options))
        {
            return Task.CompletedTask;
        }

        DevJoinTokens.Validate(_options, environment, migration.Value.EffectiveBootstrapNodeId);
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Mints the missing tokens; returns the files written.</summary>
    public async Task<IReadOnlyList<DevJoinFile>> MintAsync(CancellationToken ct)
    {
        string directory = Path.GetFullPath(_options.OutputDirectory!);
        Directory.CreateDirectory(directory);
        var written = new List<DevJoinFile>();
        foreach (string nodeId in _options.Nodes ?? [])
        {
            string path = Path.Combine(directory, DevJoinFile.FileName(nodeId));
            NodeRecord? record = await Registry.GetAsync(nodeId, ct).ConfigureAwait(false);
            if (record is { Revoked: true })
            {
                LogRevoked(logger, nodeId);
                continue;
            }

            if (record is not null && (record.CertSpkiSha256 is not null || record.PendingCertSpkiSha256 is not null))
            {
                // Enrolled already: a stale join file would only fail with "already used".
                File.Delete(path);
                LogEnrolled(logger, nodeId);
                continue;
            }

            JoinTokenIssue issue = await Registry.CreateJoinTokenAsync(nodeId, _options.TokenLifetime, JoinTokenPurpose.Join, ct).ConfigureAwait(false);
            var file = new DevJoinFile(nodeId, services.GetRequiredService<IOptions<EnrollmentOptions>>().Value.BootstrapEnrollEndpoint, Spki.ToJoinHash((services.GetService<MasterCertificates>()?.Authority ?? services.GetRequiredService<CertificateAuthority>()).SpkiSha256), issue.Token, issue.Record.ExpiresAt);
            PkiFiles.WriteSecret(path, JsonSerializer.Serialize(file, Json));
            LogMinted(logger, nodeId, path, issue.Record.ExpiresAt);
            written.Add(file);
        }

        return written;
    }

    /// <summary>Approves every configured node that enrolled and waits for approval; returns the ids approved.</summary>
    public async Task<IReadOnlyList<string>> ApprovePendingAsync(CancellationToken ct)
    {
        var approved = new List<string>();
        foreach (string nodeId in _options.Nodes ?? [])
        {
            NodeRecord? record = await Registry.GetAsync(nodeId, ct).ConfigureAwait(false);
            if (record is not { Approved: false, Revoked: false, PendingCertSpkiSha256: not null })
            {
                continue;
            }

            NodeRecord? updated = await Registry.UpdateAsync(nodeId, n => n is { Approved: false, Revoked: false, PendingCertSpkiSha256: { } pin }
                ? n with { Approved = true, ApprovedAt = time.GetUtcNow(), CertSpkiSha256 = pin, PendingCertSpkiSha256 = null }
                : n, ct).ConfigureAwait(false);
            if (updated is { Approved: true })
            {
                LogApproved(logger, nodeId, updated.CertSpkiSha256 ?? "");
                approved.Add(nodeId);
            }
        }

        return approved;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogActive(logger, string.Join(", ", _options.Nodes ?? []));
        try
        {
            await MintAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            LogFailed(logger, ex);
        }

        if (!_options.AutoApprove)
        {
            return;
        }

        TimeSpan interval = _options.ApproveInterval > TimeSpan.Zero ? _options.ApproveInterval : TimeSpan.FromSeconds(1);
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                try
                {
                    await ApprovePendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                {
                    LogFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DEVELOPMENT join-token helper is on for {Nodes}: tokens are minted and enrolled nodes approved without an operator.")]
    private static partial void LogActive(ILogger logger, string nodes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dev join token for {NodeId} written to {Path} (expires {ExpiresAt:O}).")]
    private static partial void LogMinted(ILogger logger, string nodeId, string path, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dev join token: node {NodeId} is enrolled already; no token minted.")]
    private static partial void LogEnrolled(ILogger logger, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dev join token: node {NodeId} is revoked; no token minted (delete the record to pair it again).")]
    private static partial void LogRevoked(ILogger logger, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dev join helper approved node {NodeId} (pin {Pin}).")]
    private static partial void LogApproved(ILogger logger, string nodeId, string pin);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dev join-token helper failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
