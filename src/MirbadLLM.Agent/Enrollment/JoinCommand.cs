using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Pki;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>
/// The <c>mirbadllm-agent join</c> command line (design §3.2). MN-026's <c>Program.cs</c> dispatches
/// <c>args[0] == "join"</c> here:
/// <code>
/// mirbadllm-agent join --enroll-url https://mirbad.example.com:8443 --token T --ca-hash sha256:H --node-id pc-office
///                        [--state-dir /var/lib/mirbadllm-agent] [--address 192.0.2.50]... [--force]
/// mirbadllm-agent join --rekey --enroll-url … --token R --ca-hash sha256:H --node-id pc-office [--node-uid U]
///                        [--state-dir …] [--address …]...
/// </code>
/// <c>--rekey</c> (MN-114, <see cref="AgentRekey"/>) re-pairs an enrolled node with a rekey token, keeping its uid; the uid
/// comes from the state directory or <c>--node-uid</c> (needed when the state was lost). <c>--force</c> does not apply.
/// The token may come from <c>MIRBADLLM_JOIN_TOKEN</c> instead of <c>--token</c> (keeps it out of the process list and
/// shell history). The token is never printed.
/// </summary>
public static class JoinCommand
{
    /// <summary>The verb.</summary>
    public const string Name = "join";

    /// <summary>Environment variable read when <c>--token</c> is absent.</summary>
    public const string TokenEnvironmentVariable = "MIRBADLLM_JOIN_TOKEN";

    /// <summary>Default state directory on Linux nodes.</summary>
    public const string DefaultStateDirectory = "/var/lib/mirbadllm-agent";

    /// <summary>Exit code: enrolled.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: enrollment failed.</summary>
    public const int ExitFailed = 1;

    /// <summary>Exit code: bad command line.</summary>
    public const int ExitUsage = 2;

    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: mirbadllm-agent join --enroll-url <https://host:8443> --token <token> --ca-hash <sha256:hash> --node-id <id>\n" +
        "                              [--state-dir <dir>] [--address <ip|name|https://host:port>]... [--force]\n" +
        "       mirbadllm-agent join --rekey --enroll-url <https://host:8443> --token <rekey token> --ca-hash <sha256:hash> --node-id <id>\n" +
        "                              [--node-uid <uid>] [--state-dir <dir>] [--address <ip|name|https://host:port>]...\n" +
        $"       the token may be given in {TokenEnvironmentVariable} instead of --token.";

    /// <summary>The flag that selects re-pairing (<see cref="TryParseRekey"/>).</summary>
    public const string RekeyFlag = "--rekey";

    /// <summary>
    /// Parses the arguments after <c>join</c> (without <see cref="RekeyFlag"/>). <paramref name="environmentToken"/> is the
    /// value of <see cref="TokenEnvironmentVariable"/> (null when unset). False with <paramref name="error"/> on a usage error.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, string? environmentToken, out JoinParameters? parameters, out string? error)
    {
        parameters = null;
        if (!TryParseCore(args, environmentToken, out Parsed? parsed, out error))
        {
            return false;
        }

        if (parsed!.Rekey || parsed.NodeUid is not null)
        {
            error = parsed.Rekey ? "--rekey is parsed by TryParseRekey." : "--node-uid applies only with --rekey.";
            return false;
        }

        parameters = new JoinParameters
        {
            EnrollUrl = parsed.EnrollUrl,
            Token = parsed.Token,
            CaHash = parsed.CaHash,
            NodeId = parsed.NodeId,
            StateDirectory = parsed.StateDirectory,
            Addresses = parsed.Addresses,
            Force = parsed.Force,
        };
        return true;
    }

    /// <summary>
    /// Parses <c>join --rekey …</c> (MN-114). Same arguments as <see cref="TryParse"/> plus <c>--node-uid</c>; <c>--force</c>
    /// is refused (a re-pair keeps the node uid, it never replaces another enrollment).
    /// </summary>
    public static bool TryParseRekey(IReadOnlyList<string> args, string? environmentToken, out RekeyParameters? parameters, out string? error)
    {
        parameters = null;
        if (!TryParseCore(args, environmentToken, out Parsed? parsed, out error))
        {
            return false;
        }

        if (!parsed!.Rekey)
        {
            error = "--rekey is required.";
            return false;
        }

        if (parsed.Force)
        {
            error = "--force does not apply to --rekey (a re-pair keeps the node uid; use join --force to enroll anew).";
            return false;
        }

        parameters = new RekeyParameters
        {
            EnrollUrl = parsed.EnrollUrl,
            Token = parsed.Token,
            CaHash = parsed.CaHash,
            NodeId = parsed.NodeId,
            NodeUid = parsed.NodeUid,
            StateDirectory = parsed.StateDirectory,
            Addresses = parsed.Addresses,
        };
        return true;
    }

    /// <summary>True when the arguments after <c>join</c> ask for a re-pair.</summary>
    public static bool IsRekey(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(RekeyFlag, StringComparer.Ordinal);
    }

    private sealed record Parsed(Uri EnrollUrl, string Token, string CaHash, string NodeId, string? NodeUid, string StateDirectory, List<string> Addresses, bool Force, bool Rekey);

    private static bool TryParseCore(IReadOnlyList<string> args, string? environmentToken, out Parsed? parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        parsed = null;
        error = null;
        string? url = null, token = null, caHash = null, nodeId = null, stateDir = null, nodeUid = null;
        List<string> addresses = [];
        bool force = false, rekey = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--force")
            {
                force = true;
                continue;
            }

            if (arg == RekeyFlag)
            {
                rekey = true;
                continue;
            }

            if (arg is not ("--enroll-url" or "--token" or "--ca-hash" or "--node-id" or "--state-dir" or "--address" or "--node-uid"))
            {
                error = $"Unknown argument '{(arg.StartsWith("--", StringComparison.Ordinal) ? arg : "(value)")}'.";
                return false;
            }

            if (i + 1 >= args.Count)
            {
                error = $"{arg} needs a value.";
                return false;
            }

            string value = args[++i];
            switch (arg)
            {
                case "--enroll-url": url = value; break;
                case "--token": token = value; break;
                case "--ca-hash": caHash = value; break;
                case "--node-id": nodeId = value; break;
                case "--state-dir": stateDir = value; break;
                case "--node-uid": nodeUid = value; break;
                default: addresses.Add(value); break;
            }
        }

        token ??= string.IsNullOrWhiteSpace(environmentToken) ? null : environmentToken.Trim();
        if (url is null || token is null || caHash is null || nodeId is null)
        {
            error = $"--enroll-url, --token (or {TokenEnvironmentVariable}), --ca-hash and --node-id are required.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? enrollUrl) || enrollUrl.Scheme != Uri.UriSchemeHttps)
        {
            error = "--enroll-url must be an absolute https:// URL.";
            return false;
        }

        if (!Spki.TryParseJoinHash(caHash, out _))
        {
            error = "--ca-hash must be sha256:<base64> (as shown by \"Add node\").";
            return false;
        }

        parsed = new Parsed(enrollUrl, token, caHash, nodeId, nodeUid, stateDir ?? DefaultStateDirectory, addresses, force, rekey);
        return true;
    }

    /// <summary>
    /// Runs the command: parses <paramref name="args"/> (after <c>join</c>), enrolls, prints the result. Returns the exit
    /// code. <paramref name="loggers"/> receives the enrollment log (never the token).
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        ILoggerFactory? loggers = null,
        TimeProvider? time = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(args);
        if (IsRekey(args))
        {
            return await RunRekeyAsync(args, output, error, loggers, time, ct).ConfigureAwait(false);
        }

        if (!TryParse(args, (Environment.GetEnvironmentVariable(TokenEnvironmentVariable) ?? Environment.GetEnvironmentVariable(LegacyNames.JoinTokenVariable)), out JoinParameters? parameters, out string? problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        AgentEnrollment enrollment = new(time ?? TimeProvider.System, (loggers ?? NullLoggerFactory.Instance).CreateLogger<AgentEnrollment>());
        try
        {
            EnrollmentOutcome outcome = await enrollment.JoinAsync(parameters!, ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Enrolled node '{outcome.NodeId}' (uid {outcome.NodeUid}).").ConfigureAwait(false);
            await output.WriteLineAsync($"Key fingerprint (SPKI SHA-256): {Spki.ToDisplayFingerprint(outcome.CertificateSpkiSha256)}").ConfigureAwait(false);
            await output.WriteLineAsync($"Certificate valid until {outcome.NotAfter:yyyy-MM-dd HH:mm} UTC; credentials in {parameters!.StateDirectory}.").ConfigureAwait(false);
            if (outcome.PendingApproval)
            {
                await output.WriteLineAsync("Approve the node on the master's Nodes page; compare the fingerprint above.").ConfigureAwait(false);
            }

            return ExitOk;
        }
        catch (AgentEnrollmentException ex)
        {
            await error.WriteLineAsync($"join failed ({ex.Failure}): {ex.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    private static async Task<int> RunRekeyAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        ILoggerFactory? loggers,
        TimeProvider? time,
        CancellationToken ct)
    {
        if (!TryParseRekey(args, (Environment.GetEnvironmentVariable(TokenEnvironmentVariable) ?? Environment.GetEnvironmentVariable(LegacyNames.JoinTokenVariable)), out RekeyParameters? parameters, out string? problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        AgentEnrollment enrollment = new(time ?? TimeProvider.System, (loggers ?? NullLoggerFactory.Instance).CreateLogger<AgentEnrollment>());
        try
        {
            EnrollmentOutcome outcome = await enrollment.RekeyAsync(parameters!, ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Re-paired node '{outcome.NodeId}' (uid {outcome.NodeUid} kept; models and state untouched).").ConfigureAwait(false);
            await output.WriteLineAsync($"New key fingerprint (SPKI SHA-256): {Spki.ToDisplayFingerprint(outcome.CertificateSpkiSha256)}").ConfigureAwait(false);
            await output.WriteLineAsync($"Certificate valid until {outcome.NotAfter:yyyy-MM-dd HH:mm} UTC; credentials in {parameters!.StateDirectory}.").ConfigureAwait(false);
            await output.WriteLineAsync("Restart the agent service so it serves the new certificate (running models keep running).").ConfigureAwait(false);
            if (outcome.PendingApproval)
            {
                await output.WriteLineAsync("The node is still pending approval: approve it on the master's Nodes page; compare the fingerprint above.").ConfigureAwait(false);
            }

            return ExitOk;
        }
        catch (AgentEnrollmentException ex)
        {
            await error.WriteLineAsync($"join --rekey failed ({ex.Failure}): {ex.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }
}
