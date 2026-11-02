using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using FakeDocker = MirbadLLM.UnitTests.HostServices.FakeDocker;
using FakeHostdServer = MirbadLLM.UnitTests.HostServices.FakeAgent;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable xUnit1051 // hand-written fakes; TestContext.Current.CancellationToken forwarding is not needed everywhere.

/// <summary>
/// MN-023: the agent's <c>/host/*</c> routes and host-service recovery (design §4.3 host services, §4.4 item 8):
/// the dispatched stage is durable before hostd/Docker is called, desired-state actions are reconciled after a crash,
/// restarts and backup-mount PUTs end Unknown without replay, hostd timeouts end Unknown, the share password never
/// reaches the operation records, and the agent refuses its own, the master's, model and download containers.
/// </summary>
public sealed class HostServiceEndpointsTests
{
    private const string Password = "hunter2-very-secret";

    private static readonly MasterTriple Master = new("master-a", 7, "boot-1");

    // ---------------------------------------------------------------- live requests over HTTP

    [Fact]
    public async Task StartUnitRecordsDispatchedBeforeCallingHostdAndAnswersTheOperation()
    {
        var h = new Harness();
        h.Hostd.Units["comfyui"] = h.Hostd.Units["comfyui"] with { ActiveState = "inactive", SubState = "dead" };
        string? stageAtCall = null;
        h.Hostd.OnMutation = _ => stageAtCall = h.Log.Find("op-1")?.Stage;
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await h.PostAsync(client, "/host/services/comfyui/start", Envelope("op-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(OperationKinds.ServiceStart, status.Kind);
        Assert.Equal("comfyui", status.Target);
        Assert.Equal("active", status.ReadResult<HostUnit>()!.ActiveState);
        Assert.Equal(OperationStages.Dispatched, stageAtCall);
        Assert.Equal([OperationStages.Dispatched], h.Log.Stages("op-1"));
        Assert.Equal(["run comfyui Start"], h.Hostd.Calls);
    }

    [Fact]
    public async Task RepeatedOperationIdReturnsTheStoredStatusWithoutCallingHostdAgain()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();
        var body = Envelope("op-dup");

        using var first = await h.PostAsync(client, "/host/services/comfyui/restart", body);
        using var second = await h.PostAsync(client, "/host/services/comfyui/restart", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync<OperationStatus>(first)).EndedAt, (await ReadAsync<OperationStatus>(second)).EndedAt);
        Assert.Single(h.Hostd.Calls);
    }

    [Fact]
    public async Task SameIdWithAnotherPayloadIsAConflictAndRunsNothing()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var first = await h.PostAsync(client, "/host/services/comfyui/stop", Envelope("op-x"));
        using var second = await h.PostAsync(client, "/host/services/comfyui/stop", Envelope("op-x") with { Confirm = "comfyui" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertErrorAsync(second, HttpStatusCode.Conflict, ErrorCodes.OperationIdConflict);
        Assert.Single(h.Hostd.Calls);
    }

    [Fact]
    public async Task ExpiredEnvelopeIs410AndNothingRuns()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();
        var now = h.Time.GetUtcNow();

        using var response = await h.PostAsync(client, "/host/services/comfyui/stop",
            new HostServiceActionRequest { OperationId = "op-old", IssuedAt = now.AddHours(-25), ExpiresAt = now.AddHours(-1) });

        await AssertErrorAsync(response, HttpStatusCode.Gone, ErrorCodes.OperationExpired);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public async Task MissingMasterHeaderIs400AndStaleMasterIs409WithNothingRecorded()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var noHeader = await client.PostAsJsonAsync(Protocol.BasePath + "/host/services/comfyui/stop", Envelope("op-a"), AgentJson.Options);
        using var stale = await h.PostAsync(client, "/host/services/comfyui/stop", Envelope("op-b"), Master with { Epoch = 3 });

        await AssertErrorAsync(noHeader, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, ErrorCodes.StaleMaster);
        Assert.Empty(h.Log.Records);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public async Task HostdRefusalsKeepTheirCodesAndFailTheOperation()
    {
        var h = new Harness();
        h.Hostd.NextFailure = new HostServiceConfirmationRequiredException("'rustdesk' is protected: type its name to stop it.");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await h.PostAsync(client, "/host/services/rustdesk/stop", Envelope("op-p"));

        var error = await AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
        Assert.Contains("type its name", error.Message, StringComparison.Ordinal);
        Assert.Equal(OperationState.Failed, h.Log.Find("op-p")!.State);
    }

    [Theory]
    [InlineData("restart", OperationKinds.ServiceRestart)]
    [InlineData("start", OperationKinds.ServiceStart)]
    public async Task HostdTimeoutEndsUnknownWith504AndTheObservedState(string verb, string kind)
    {
        var h = new Harness();
        h.Hostd.NextFailure = new HostdTimeoutException("mirbadllm-hostd did not answer in time; the outcome is unknown.");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await h.PostAsync(client, $"/host/services/comfyui/{verb}", Envelope("op-t"));

        var error = await AssertErrorAsync(response, HttpStatusCode.GatewayTimeout, ErrorCodes.OutcomeUnknown);
        Assert.Contains("Outcome unknown", error.Message, StringComparison.Ordinal);
        Assert.Contains("Observed: active (running)", error.Message, StringComparison.Ordinal);
        var status = h.Log.Find("op-t")!;
        Assert.Equal(OperationState.Unknown, status.State);
        Assert.Equal(kind, status.Kind);
        Assert.Equal("comfyui", status.ReadResult<HostUnit>()!.Name);
    }

    [Fact]
    public async Task MissingHostdSocketFailsWithHostdUnavailableNotUnknown()
    {
        var h = new Harness();
        h.Hostd.NextFailure = new HostAgentUnavailableException("The host agent is not installed (no socket).");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await h.PostAsync(client, "/host/services/comfyui/restart", Envelope("op-u"));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.HostdUnavailable);
        Assert.Equal(OperationState.Failed, h.Log.Find("op-u")!.State);
    }

    [Fact]
    public async Task OperationLogFailureAtAcceptanceIs503AndNothingRuns()
    {
        var h = new Harness();
        h.Log.FailAccept = true;
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await h.PostAsync(client, "/host/services/comfyui/stop", Envelope("op-s"));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.StateUnavailable);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public async Task SharePasswordPassesToHostdOnceAndNeverReachesTheOperationRecords()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();
        var request = new BackupMountSetRequest
        {
            OperationId = "op-mount",
            IssuedAt = h.Time.GetUtcNow(),
            ExpiresAt = h.Time.GetUtcNow().AddHours(24),
            Settings = new BackupMountSettings { Type = "smb", Server = "nas", Share = "backups", Username = "svc", Password = Password },
        };

        using var response = await h.PutAsync(client, "/host/backup-mount", request);
        string responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Password], h.Hostd.PasswordsReceived);
        Assert.DoesNotContain(Password, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, h.Log.AllRecordedText(), StringComparison.Ordinal);
        Assert.Contains(Protocol.RedactedValue, h.Log.AllRecordedText(), StringComparison.Ordinal);
        Assert.True(h.Log.Find("op-mount")!.ReadResult<BackupMountInfo>()!.Mounted);
    }

    [Fact]
    public async Task BackupMountTestIsNotAnOperation()
    {
        var h = new Harness();
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(Protocol.BasePath + "/host/backup-mount/test",
            new BackupMountTestRequest { Settings = new BackupMountSettings { Type = "smb", Server = "nas", Share = "b", Password = Password } },
            AgentJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync<BackupMountTestResult>(response)).Ok);
        Assert.Empty(h.Log.Records);
        Assert.Equal([Password], h.Hostd.PasswordsReceived);
    }

    // ---------------------------------------------------------------- crash recovery (§4.4 item 8)

    [Fact]
    public async Task StopDispatchedThenCrashIsReconciledSucceededWithoutReplay()
    {
        var h = new Harness();
        h.Log.CrashOnComplete = true; // hostd stops the unit; the agent dies before the terminal record
        var result = await h.SubmitAsync(Mutation(OperationKinds.ServiceStop, "comfyui", Envelope("op-stop")));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(OperationStages.Dispatched, h.Log.Find("op-stop")!.Stage);
        Assert.Equal("inactive", h.Hostd.Units["comfyui"].ActiveState);

        await h.RestartAndRecoverAsync();

        var status = h.Log.Find("op-stop")!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal("inactive", status.ReadResult<HostUnit>()!.ActiveState);
        Assert.Empty(h.Hostd.Calls); // the stop already took effect: not sent again
    }

    [Fact]
    public async Task StopDispatchedButNotAppliedIsAppliedAgainAfterCrash()
    {
        var h = new Harness();
        await h.SeedAsync(OperationKinds.ServiceStop, "comfyui", Envelope("op-stop2"), dispatched: true);

        await h.RestartAndRecoverAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-stop2")!.State);
        Assert.Equal("inactive", h.Hostd.Units["comfyui"].ActiveState);
        Assert.Equal(["run comfyui Stop"], h.Hostd.Calls);
    }

    [Fact]
    public async Task AutostartAndWhitelistActionsAreReconciledByDesiredState()
    {
        var h = new Harness();
        await h.SeedAsync(OperationKinds.ServiceAutostart, "comfyui",
            new HostServiceAutostartRequest { OperationId = "op-auto", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24), Enabled = true },
            dispatched: true);
        await h.SeedAsync(OperationKinds.WhitelistRemove, "lmstudio-server",
            new HostWhitelistRemoveRequest { OperationId = "op-rm", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24) },
            dispatched: true);
        h.Hostd.Units.Remove("lmstudio-server"); // the removal had reached hostd before the crash

        await h.RestartAndRecoverAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-auto")!.State); // already enabled: nothing sent
        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-rm")!.State);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public async Task RestartDispatchedThenCrashEndsUnknownAndIsNotReplayed()
    {
        var h = new Harness();
        h.Log.CrashOnComplete = true;
        await h.SubmitAsync(Mutation(OperationKinds.ServiceRestart, "comfyui", Envelope("op-restart")));
        Assert.Equal(1, h.Hostd.Restarts);

        await h.RestartAndRecoverAsync();

        var status = h.Log.Find("op-restart")!;
        Assert.Equal(OperationState.Unknown, status.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, status.Error!.Code);
        Assert.Contains("Outcome unknown", status.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Observed: active (running)", status.Error.Message, StringComparison.Ordinal);
        Assert.Equal("comfyui", status.ReadResult<HostUnit>()!.Name);
        Assert.Equal(1, h.Hostd.Restarts);
    }

    [Fact]
    public async Task RestartAcceptedButNotDispatchedFailsWithoutCallingHostd()
    {
        var h = new Harness();
        await h.SeedAsync(OperationKinds.ServiceRestart, "comfyui", Envelope("op-r0"), dispatched: false);

        await h.RestartAndRecoverAsync();

        var status = h.Log.Find("op-r0")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Contains("nothing was changed", status.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public async Task BackupMountPutDispatchedThenCrashEndsUnknownAndIsNotReplayed()
    {
        var h = new Harness();
        h.Log.CrashOnComplete = true;
        await h.SubmitAsync(new HostMutation(OperationKinds.BackupMountSet, HostMutation.BackupMountTarget, new BackupMountSetRequest
        {
            OperationId = "op-put",
            IssuedAt = h.Time.GetUtcNow(),
            ExpiresAt = h.Time.GetUtcNow().AddHours(24),
            Settings = new BackupMountSettings { Type = "smb", Server = "nas", Share = "backups", Password = Password },
        }));

        await h.RestartAndRecoverAsync();

        var status = h.Log.Find("op-put")!;
        Assert.Equal(OperationState.Unknown, status.State);
        Assert.Contains("mounted from", status.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(1, h.Hostd.MountSets);
        Assert.DoesNotContain(Password, h.Log.AllRecordedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupMountDeleteIsReconciled()
    {
        var h = new Harness();
        h.Hostd.Mount = h.Hostd.Mount with { Configured = true, Mounted = true };
        await h.SeedAsync(OperationKinds.BackupMountRemove, HostMutation.BackupMountTarget,
            new BackupMountRemoveRequest { OperationId = "op-del", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24) },
            dispatched: true);

        await h.RestartAndRecoverAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-del")!.State);
        Assert.False(h.Hostd.Mount.Mounted);
        Assert.Equal(["backup-mount remove"], h.Hostd.Calls);
    }

    [Fact]
    public async Task RecoveryDoesNotReapplyForASupersededMasterOrAnExpiredEnvelope()
    {
        var h = new Harness();
        await h.SeedAsync(OperationKinds.ServiceStop, "comfyui", Envelope("op-old-master"), dispatched: true);
        await h.SeedAsync(OperationKinds.ServiceStop, "rustdesk",
            new HostServiceActionRequest { OperationId = "op-expiring", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddMinutes(1), Confirm = "rustdesk" },
            dispatched: true);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Fencing.AcceptedTriple = Master with { Epoch = 8, BootId = "boot-2" }; // a newer master took over

        await h.RestartAndRecoverAsync();

        var superseded = h.Log.Find("op-old-master")!;
        var expired = h.Log.Find("op-expiring")!;
        Assert.Equal(OperationState.Failed, superseded.State);
        Assert.Equal(ErrorCodes.Superseded, superseded.Error!.Code);
        Assert.Equal(OperationState.Failed, expired.State);
        Assert.Equal(ErrorCodes.OperationExpired, expired.Error!.Code);
        Assert.Empty(h.Hostd.Calls);
    }

    [Fact]
    public void RecoveryHandlerClaimsEveryHostServiceKind()
    {
        var h = new Harness();

        var kinds = h.Recovery().Kinds;

        Assert.Equal(OperationKinds.DesiredState.Concat(OperationKinds.Unverifiable).Order(), kinds.Order());
        Assert.All(kinds, k => Assert.NotNull(HostMutation.RequestTypeOf(k)));
    }

    // ---------------------------------------------------------------- containers

    [Theory]
    [InlineData("mirbadllm", null)]
    [InlineData("mirbadllm-agent", null)]
    [InlineData("ss-qwen36-35b-a3b-nvfp4", ContainerLabels.Model)]
    [InlineData("ss-tuning-candidate", ContainerLabels.Tuning)]
    [InlineData("hf-download-qwen", ContainerLabels.Download)]
    public async Task AgentRefusesOwnMasterModelAndDownloadContainers(string name, string? label)
    {
        var h = new Harness();
        h.Docker.Add(name, c =>
        {
            if (label is not null)
            {
                c.Labels[label] = "x";
            }
        });
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var stop = await h.PostAsync(client, $"/host/containers/{name}/stop", new HostContainerActionRequest
        {
            OperationId = "op-c", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24),
        });
        using var get = await client.GetAsync($"{Protocol.BasePath}/host/containers/{name}");
        var list = await client.GetFromJsonAsync<HostServicesResponse>(Protocol.BasePath + "/host/services?include=all", AgentJson.Options);

        await AssertErrorAsync(stop, HttpStatusCode.Forbidden, ErrorCodes.Denied);
        await AssertErrorAsync(get, HttpStatusCode.Forbidden, ErrorCodes.Denied);
        Assert.DoesNotContain(list!.Containers, c => c.Name == name);
        Assert.Empty(h.Docker.Posts);
        Assert.Equal(OperationState.Failed, h.Log.Find("op-c")!.State);
        Assert.Empty(h.Log.Stages("op-c")); // never dispatched
    }

    [Fact]
    public void ContainerRulesKeepTodaysUnlabelledLegacyContainersManageable()
    {
        var options = new AgentHostServicesOptions();

        Assert.False(HostContainerRules.IsDenied("ss-vllm", new Dictionary<string, string>(), options));
        Assert.False(HostContainerRules.IsDenied("openviking", new Dictionary<string, string> { ["com.docker.compose.project"] = "ov" }, options));
        Assert.True(HostContainerRules.IsDenied("ss-anything", new Dictionary<string, string> { [ContainerLabels.ServedName] = "x" }, options));
        Assert.True(HostContainerRules.IsDenied("other", new Dictionary<string, string> { [ContainerLabels.Node] = "dgx" }, options));
        Assert.True(HostContainerRules.IsDenied("/mirbadllm", null, options));
    }

    [Fact]
    public async Task ListsUnitsAndNonModelContainersWithMemory()
    {
        var h = new Harness();
        h.Docker.Add("openviking");
        h.Docker.Add("ss-vllm", c => c.Status = "exited");
        h.Docker.Add("ss-qwen3-embedding-4b", c => c.Labels[ContainerLabels.Model] = "qwen3-embedding-4b");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        var managed = await client.GetFromJsonAsync<HostServicesResponse>(Protocol.BasePath + "/host/services", AgentJson.Options);
        var all = await client.GetFromJsonAsync<HostServicesResponse>(Protocol.BasePath + "/host/services?include=all", AgentJson.Options);

        Assert.True(managed!.HostdAvailable);
        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk"], managed.Units.Select(u => u.Name).Order());
        Assert.Null(managed.Discovered);
        Assert.Equal(["openviking", "ss-vllm"], managed.Containers.Select(c => c.Name).Order());
        var ov = managed.Containers.Single(c => c.Name == "openviking");
        Assert.Equal(500L * 1024 * 1024, ov.MemoryBytes);
        Assert.Equal("always", ov.RestartPolicy);
        Assert.Null(managed.Containers.Single(c => c.Name == "ss-vllm").MemoryBytes);
        Assert.NotNull(all!.Discovered);
        Assert.Equal(["ssh*"], all.DenyPatterns);
    }

    [Fact]
    public async Task ListReportsHostdDownInTheBodyAsTheServicesPageShowsIt()
    {
        var h = new Harness();
        h.Hostd.ReadFailure = new HostAgentUnavailableException("The host agent is not installed (no socket).");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetFromJsonAsync<HostServicesResponse>(Protocol.BasePath + "/host/services", AgentJson.Options);

        Assert.False(response!.HostdAvailable);
        Assert.Equal("The host agent is not installed (no socket).", response.HostdError);
        Assert.Empty(response.Units);
        Assert.True(response.ContainersAvailable);
    }

    [Fact]
    public async Task ContainerStopAndRestartPolicySucceedWithTheContainerRow()
    {
        var h = new Harness();
        h.Docker.Add("openviking");
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();
        var now = h.Time.GetUtcNow();

        using var stop = await h.PostAsync(client, "/host/containers/openviking/stop",
            new HostContainerActionRequest { OperationId = "op-cs", IssuedAt = now, ExpiresAt = now.AddHours(24), TimeoutSeconds = 10 });
        using var policy = await h.PostAsync(client, "/host/containers/openviking/autostart",
            new HostContainerAutostartRequest { OperationId = "op-cp", IssuedAt = now, ExpiresAt = now.AddHours(24), RestartPolicy = "no" });
        using var badPolicy = await h.PostAsync(client, "/host/containers/openviking/autostart",
            new HostContainerAutostartRequest { OperationId = "op-cb", IssuedAt = now, ExpiresAt = now.AddHours(24), RestartPolicy = "sometimes" });

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal("exited", (await ReadAsync<OperationStatus>(stop)).ReadResult<HostContainerInfo>()!.State);
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        Assert.Equal("no", (await ReadAsync<OperationStatus>(policy)).ReadResult<HostContainerInfo>()!.RestartPolicy);
        await AssertErrorAsync(badPolicy, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        Assert.Equal(["stop openviking", "update openviking"], h.Docker.Posts);
        Assert.Equal("POST", h.Docker.Handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal)).Method.Method);
        Assert.Contains("t=10", h.Docker.Handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal)).RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerStartDispatchedThenCrashIsReconciledAndRestartIsUnknown()
    {
        var h = new Harness();
        h.Docker.Add("n8n-compose-n8n-1", c => c.Status = "exited");
        h.Log.CrashOnComplete = true;
        var now = h.Time.GetUtcNow();
        await h.SubmitAsync(new HostMutation(OperationKinds.ContainerStart, "n8n-compose-n8n-1",
            new HostContainerActionRequest { OperationId = "op-cstart", IssuedAt = now, ExpiresAt = now.AddHours(24) }));
        await h.SubmitAsync(new HostMutation(OperationKinds.ContainerRestart, "n8n-compose-n8n-1",
            new HostContainerActionRequest { OperationId = "op-crestart", IssuedAt = now, ExpiresAt = now.AddHours(24) }));
        Assert.Equal(["start n8n-compose-n8n-1", "restart n8n-compose-n8n-1"], h.Docker.Posts);

        await h.RestartAndRecoverAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-cstart")!.State);
        Assert.Equal(OperationState.Unknown, h.Log.Find("op-crestart")!.State);
        Assert.Empty(h.Docker.Posts); // nothing replayed
    }

    // ---------------------------------------------------------------- hostd client and contributors

    [Fact]
    public async Task HostdClientMapsUnitsDenyAndTimeout()
    {
        var server = new FakeHostdServer();
        var client = new HostdClient(server.Client(), () => true);

        var units = await client.ListAsync();
        var denied = await Assert.ThrowsAsync<HostServiceDeniedException>(() =>
            client.AddToWhitelistAsync("ssh.service", "system", null, isProtected: false, confirm: "ssh"));
        server.Handler.Handler = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
        var timeout = await Assert.ThrowsAsync<HostdTimeoutException>(() => client.RunAsync("comfyui", HostServiceAction.Restart, null));

        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk"], units.Select(u => u.Name));
        Assert.True(units.Single(u => u.Name == "rustdesk").Protected);
        Assert.Contains("cannot be managed", denied.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.Timeout, HostServiceErrors.FromHostd(timeout).Error.Code);
        Assert.True(HostServiceErrors.FromHostd(timeout).Ambiguous);
        Assert.False(HostServiceErrors.FromHostd(denied).Ambiguous);
    }

    [Fact]
    public async Task ContributorReportsFeaturesAndHostdMissingOnANodeThatHadIt()
    {
        var hostd = new ScriptedHostd();
        var contributor = new HostServicesContributor(hostd, new AgentHostServicesOptions(), dockerAvailable: true);
        var baseReport = new NodeCapabilities
        {
            Protocol = Protocol.Range,
            AgentVersion = "1.0.0",
            Features = [AgentFeatures.TelemetryV1],
            Os = new NodeOsInfo("linux", "Ubuntu"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
        };

        var report = await contributor.ContributeAsync(baseReport);
        var healthy = await contributor.CheckAsync();
        hostd.Installed = false;
        var degraded = await contributor.CheckAsync();
        var whileDown = await contributor.ContributeAsync(baseReport);

        Assert.Equal(
            [AgentFeatures.TelemetryV1, AgentFeatures.ServicesSystemd, AgentFeatures.GpuProcesses, AgentFeatures.ServicesDocker, AgentFeatures.BackupMount],
            report.Features);
        Assert.Equal(new HostServicesCapability("systemd", true, null, true), report.HostServices);
        Assert.True(report.BackupMount!.Supported);
        Assert.Empty(healthy);
        Assert.Equal(HealthReasonCodes.HostdMissing, Assert.Single(degraded).Code);
        Assert.False(whileDown.HostServices!.HostdAvailable);
        Assert.Contains(AgentFeatures.ServicesSystemd, whileDown.Features);
    }

    [Fact]
    public async Task UnsupportedFeatureIs501()
    {
        var h = new Harness(o => o.BackupMount = false);
        await using var app = await h.StartAsync();
        using var client = app.GetTestClient();

        using var get = await client.GetAsync(Protocol.BasePath + "/host/backup-mount");
        using var remove = await h.SendAsync(client, HttpMethod.Delete, "/host/backup-mount",
            new BackupMountRemoveRequest { OperationId = "op-n", IssuedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(24) });

        await AssertErrorAsync(get, HttpStatusCode.NotImplemented, ErrorCodes.NotSupported);
        await AssertErrorAsync(remove, HttpStatusCode.NotImplemented, ErrorCodes.NotSupported);
        Assert.Empty(h.Log.Records);
    }

    // ---------------------------------------------------------------- helpers

    private static HostServiceActionRequest Envelope(string id) =>
        new() { OperationId = id, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };

    private static HostMutation Mutation(string kind, string target, AgentMutation request) => new(kind, target, request);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(AgentJson.Options))!;

    private static async Task<AgentError> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await ReadAsync<AgentErrorResponse>(response);
        Assert.Equal(code, body.Error.Code);
        return body.Error;
    }

    /// <summary>The agent's host-service package over fakes, as one process; <see cref="RestartAndRecoverAsync"/> simulates a new process.</summary>
    private sealed class Harness
    {
        private readonly AgentHostServicesOptions _options = new();

        public Harness(Action<AgentHostServicesOptions>? configure = null)
        {
            configure?.Invoke(_options);
            Log = new FakeOperationLog(Time);
            Fencing.AcceptedTriple = Master;
            Operations = NewOperations();
        }

        public ManualTimeProvider Time { get; } = new();

        /// <summary>The triple HTTP requests carry unless a test passes another one.</summary>
        public MasterTriple SubmittingMaster { get; set; } = Master;

        public FakeOperationLog Log { get; }

        public FakeFencing Fencing { get; } = new();

        public ScriptedHostd Hostd { get; } = new();

        public FakeDocker Docker { get; } = new();

        public HostServiceOperations Operations { get; private set; }

        public HostServiceRecoveryHandler Recovery() =>
            new(Operations, Log, Fencing, Time, NullLogger<HostServiceRecoveryHandler>.Instance);

        public async Task<WebApplication> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddRouting();
            builder.Services.AddSingleton<IHostdClient>(Hostd);
            builder.Services.AddSingleton(new HostContainers(Docker.Client(), _options));
            builder.Services.AddSingleton<IOperationLog>(Log);
            builder.Services.AddSingleton<IFencingState>(Fencing);
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.Services.AddAgentHostServices(o =>
            {
                o.Systemd = _options.Systemd;
                o.Containers = _options.Containers;
                o.BackupMount = _options.BackupMount;
            });
            var app = builder.Build();
            app.MapGroup(Protocol.BasePath).MapHostServiceEndpoints();
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }

        public Task<HostCallResult> SubmitAsync(HostMutation mutation) =>
            Operations.SubmitAsync(mutation, Master.ToHeaderValue(), JsonSerializer.SerializeToElement(mutation.Request, mutation.Request.GetType(), AgentJson.Options));

        /// <summary>Records an operation as accepted (and dispatched) without running it: the agent died right there.</summary>
        public async Task SeedAsync(string kind, string target, AgentMutation request, bool dispatched)
        {
            var payload = JsonSerializer.SerializeToElement(request, request.GetType(), AgentJson.Options);
            await Log.AcceptAsync(new OperationSubmission
            {
                OperationId = request.OperationId, Kind = kind, Target = target, IssuedAt = request.IssuedAt, ExpiresAt = request.ExpiresAt,
                Master = Master, Payload = payload,
            });
            if (dispatched)
            {
                await Log.RecordStageAsync(request.OperationId, new OperationStageUpdate { Stage = OperationStages.Dispatched, State = OperationState.Running });
            }
        }

        /// <summary>A new agent process: fresh executor and handler over the same durable log, then recovery of every open op.</summary>
        public async Task RestartAndRecoverAsync()
        {
            Log.CrashOnComplete = false;
            Hostd.Calls.Clear();
            Docker.Posts.Clear();
            Operations = NewOperations();
            var handler = Recovery();
            foreach (var operation in Log.ListRecoverable())
            {
                Assert.Contains(operation.Status.Kind, handler.Kinds);
                await handler.RecoverAsync(operation);
            }

            Assert.Empty(Log.ListActive());
        }

        public Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, MasterTriple? master = null) =>
            SendAsync(client, HttpMethod.Post, path, body, master);

        public Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body) =>
            SendAsync(client, HttpMethod.Put, path, body);

        public Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body, MasterTriple? master = null)
        {
            var request = new HttpRequestMessage(method, Protocol.BasePath + path)
            {
                Content = JsonContent.Create(body, body.GetType(), options: AgentJson.Options),
            };
            request.Headers.Add(Protocol.MasterHeader, (master ?? SubmittingMaster).ToHeaderValue());
            return client.SendAsync(request);
        }

        private HostServiceOperations NewOperations() =>
            new(Hostd, _options.Containers ? new HostContainers(Docker.Client(), _options) : null, Log, Fencing, _options, Time,
                NullLogger<HostServiceOperations>.Instance);
    }
}

/// <summary>A scripted hostd (units, whitelist, backup mount) that records every mutating call.</summary>
internal sealed class ScriptedHostd : IHostdClient
{
    public Dictionary<string, HostUnit> Units { get; } = new(StringComparer.Ordinal)
    {
        ["comfyui"] = Unit("comfyui", "comfyui.service", isProtected: false),
        ["lmstudio-server"] = Unit("lmstudio-server", "lmstudio-server.service", isProtected: false),
        ["rustdesk"] = Unit("rustdesk", "rustdesk.service", isProtected: true),
    };

    public BackupMountInfo Mount { get; set; } = new() { Target = "/srv/mirbadllm/backups", Unit = "srv-mirbadllm-backups.mount" };

    public List<string> Calls { get; } = [];

    public List<string?> PasswordsReceived { get; } = [];

    public int Restarts { get; private set; }

    public int MountSets { get; private set; }

    public bool Installed { get; set; } = true;

    /// <summary>Thrown by the next mutating call instead of acting.</summary>
    public Exception? NextFailure { get; set; }

    /// <summary>Thrown by reads (list, discover).</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>Called at the start of every mutating call, before anything happens.</summary>
    public Action<string>? OnMutation { get; set; }

    public bool IsInstalled => Installed;

    public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default) =>
        ReadFailure is { } f ? Task.FromException<IReadOnlyList<HostUnit>>(f) : Task.FromResult<IReadOnlyList<HostUnit>>([.. Units.Values]);

    public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default) =>
        ReadFailure is { } f
            ? Task.FromException<HostUnitDiscovery>(f)
            : Task.FromResult(new HostUnitDiscovery
            {
                Services = [.. Units.Values],
                Discovered = [new HostDiscoveredUnit { Name = "ssh", Unit = "ssh.service", Scope = "system", Denied = true, DeniedBy = "ssh*" }],
                DenyPatterns = ["ssh*"],
            });

    public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default) =>
        Mutate($"whitelist add {unit}", () =>
        {
            var added = Unit(confirm ?? unit, unit, isProtected) with { Scope = scope, DisplayName = displayName ?? unit };
            Units[added.Name] = added;
            return added;
        });

    public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default) =>
        Mutate($"whitelist update {name}", () =>
        {
            var u = Find(name);
            Units[name] = u with { DisplayName = displayName ?? u.DisplayName, Protected = isProtected ?? u.Protected };
            return Units[name];
        });

    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) =>
        Mutate($"whitelist remove {name}", () =>
        {
            var u = Find(name);
            Units.Remove(name);
            return u;
        });

    public Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default) =>
        Mutate($"run {name} {action}", () =>
        {
            var u = Find(name);
            if (action == HostServiceAction.Restart)
            {
                Restarts++;
            }

            Units[name] = action == HostServiceAction.Stop
                ? u with { ActiveState = "inactive", SubState = "dead" }
                : u with { ActiveState = "active", SubState = "running" };
            return Units[name];
        });

    public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default) =>
        Mutate($"autostart {name} {enabled}", () =>
        {
            Units[name] = Find(name) with { UnitFileState = enabled ? "enabled" : "disabled" };
            return Units[name];
        });

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GpuProcess>?>([new GpuProcess(276633, "python3", 40144, null, "comfyui.service", "ComfyUI")]);

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => Task.FromResult(Mount);

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default)
    {
        PasswordsReceived.Add(settings.Password);
        return Task.FromResult(new BackupMountTestResult(true, null));
    }

    public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) =>
        Mutate("backup-mount set", () =>
        {
            PasswordsReceived.Add(settings.Password);
            MountSets++;
            Mount = Mount with
            {
                Configured = true, Mounted = true, Type = settings.Type, Server = settings.Server, Share = settings.Share,
                Source = $"//{settings.Server}/{settings.Share}", MountedSource = $"//{settings.Server}/{settings.Share}",
            };
            return Mount;
        });

    public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default) =>
        Mutate("backup-mount remove", () =>
        {
            Mount = Mount with { Configured = false, Mounted = false, Source = null, MountedSource = null };
            return Mount;
        });

    private Task<T> Mutate<T>(string call, Func<T> act)
    {
        OnMutation?.Invoke(call);
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            return Task.FromException<T>(failure);
        }

        Calls.Add(call);
        return Task.FromResult(act());
    }

    private HostUnit Find(string name) =>
        Units.TryGetValue(name, out var u) ? u : throw new HostServiceNotFoundException($"'{name}' is not in the allowlist.");

    private static HostUnit Unit(string name, string unit, bool isProtected) => new()
    {
        Name = name,
        Unit = unit,
        Scope = "system",
        DisplayName = name,
        Protected = isProtected,
        ActiveState = "active",
        SubState = "running",
        UnitFileState = "enabled",
        MainPid = 42,
        MemoryBytes = 1_649_680_384,
    };
}

/// <summary>
/// An in-memory operation log with the §4.4 acceptance rules (dedup by id + kind + target + digest, expiry, future
/// issuedAt). It stores payloads exactly as given — no redaction of its own — so tests see what the caller handed over.
/// Knobs: <see cref="FailAccept"/>, <see cref="CrashOnComplete"/> (the terminal record is lost, as when the agent dies).
/// </summary>
internal sealed class FakeOperationLog(TimeProvider time) : IOperationLog
{
    public sealed class Record
    {
        public required OperationSubmission Submission { get; init; }

        public required OperationStatus Status { get; set; }

        public List<string> Stages { get; } = [];
    }

    public Dictionary<string, Record> Records { get; } = new(StringComparer.Ordinal);

    public bool FailAccept { get; set; }

    public bool CrashOnComplete { get; set; }

    public int TornLinesIgnored => 0;

    public IReadOnlyList<string> Stages(string id) => Records.TryGetValue(id, out var r) ? r.Stages : [];

    public string AllRecordedText() => string.Join('\n', Records.Values.Select(r =>
        JsonSerializer.Serialize(r.Submission.Payload, AgentJson.Options) + JsonSerializer.Serialize(r.Status, AgentJson.Options)));

    public Task<OperationAcceptance> AcceptAsync(OperationSubmission submission, CancellationToken ct = default)
    {
        if (FailAccept)
        {
            throw new IOException("disk full");
        }

        string digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(submission.Payload.GetRawText())));
        if (Records.TryGetValue(submission.OperationId, out var existing))
        {
            bool same = existing.Status.Kind == submission.Kind && existing.Status.Target == submission.Target && existing.Status.PayloadDigest == digest;
            return Task.FromResult(same
                ? new OperationAcceptance(OperationAcceptOutcome.Duplicate, existing.Status)
                : new OperationAcceptance(OperationAcceptOutcome.Conflict, null));
        }

        var now = time.GetUtcNow();
        if (submission.ExpiresAt <= now)
        {
            return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Expired, null));
        }

        if (submission.IssuedAt > now + OperationRetention.MaxIssuedAtFutureSkew)
        {
            return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.IssuedInFuture, null));
        }

        var status = new OperationStatus
        {
            Id = submission.OperationId, Kind = submission.Kind, Target = submission.Target, PayloadDigest = digest,
            State = OperationState.Accepted, Stage = OperationStages.Accepted, IssuedAt = submission.IssuedAt,
            ExpiresAt = submission.ExpiresAt, StartedAt = now,
        };
        Records[submission.OperationId] = new Record { Submission = submission, Status = status };
        return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Accepted, status));
    }

    public Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default)
    {
        var record = Records[operationId];
        if (update.Stage is { } stage)
        {
            record.Stages.Add(stage);
        }

        record.Status = record.Status with
        {
            Stage = update.Stage ?? record.Status.Stage,
            State = update.State ?? (record.Status.State == OperationState.Accepted ? OperationState.Running : record.Status.State),
            Progress = update.Progress ?? record.Status.Progress,
        };
        return Task.FromResult(record.Status);
    }

    public Task<OperationStatus> CompleteAsync(string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default)
    {
        if (CrashOnComplete)
        {
            throw new IOException("simulated crash: the terminal record was not written");
        }

        var record = Records[operationId];
        if (IsTerminal(record.Status.State))
        {
            return Task.FromResult(record.Status);
        }

        record.Status = record.Status with
        {
            State = state, Stage = OperationStages.Completed, Error = failure, Result = result, EndedAt = time.GetUtcNow(),
        };
        return Task.FromResult(record.Status);
    }

    public OperationStatus? Find(string operationId) => Records.TryGetValue(operationId, out var r) ? r.Status : null;

    public IReadOnlyList<OperationStatus> ListActive() => [.. Records.Values.Where(r => !IsTerminal(r.Status.State)).Select(r => r.Status)];

    public IReadOnlyList<OperationStatus> ListRecent(int max) =>
        [.. Records.Values.Where(r => IsTerminal(r.Status.State)).Select(r => r.Status).OrderByDescending(s => s.EndedAt).Take(max)];

    public IReadOnlyList<RecoverableOperation> ListRecoverable() =>
        [.. Records.Values.Where(r => !IsTerminal(r.Status.State)).Select(r => new RecoverableOperation
        {
            Status = r.Status, Master = r.Submission.Master, Payload = r.Submission.Payload,
        })];

    public Task CompactAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static bool IsTerminal(OperationState state) =>
        state is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Unknown;
}

/// <summary>Fencing with the §4.2 decision table over one accepted triple.</summary>
internal sealed class FakeFencing : IFencingState
{
    public MasterTriple? AcceptedTriple { get; set; }

    public MasterTriple? Accepted => AcceptedTriple;

    public Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default)
    {
        if (AcceptedTriple is null || presented.Epoch > AcceptedTriple.Epoch)
        {
            AcceptedTriple = presented;
            return Task.FromResult(new FencingCheck(FencingDecision.Accepted, presented));
        }

        if (presented.Epoch < AcceptedTriple.Epoch)
        {
            return Task.FromResult(new FencingCheck(FencingDecision.StaleMaster, AcceptedTriple));
        }

        return Task.FromResult(presented.SameBoot(AcceptedTriple)
            ? new FencingCheck(FencingDecision.Accepted, AcceptedTriple)
            : new FencingCheck(FencingDecision.CompetingMaster, AcceptedTriple));
    }

    public bool IsCurrent(MasterTriple operationMaster) => operationMaster.SameBoot(AcceptedTriple);
}
