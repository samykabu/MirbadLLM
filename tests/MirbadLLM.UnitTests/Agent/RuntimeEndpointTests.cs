using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.UnitTests.Orchestration.Fakes;

namespace MirbadLLM.UnitTests.Agent.RuntimeOps;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-031: the agent's <c>/runtimes/*</c> and <c>/operations/*</c> routes over fakes of the operation log, fencing state,
/// reservation manager, event bus and runtime driver (design §4.2–§4.8).
/// </summary>
public sealed class RuntimeEndpointTests
{
    // ---- launch: acceptance, idempotency, envelope (design §4.3, §4.4) ----

    [Fact]
    public async Task Launch_New_Answers202AfterDurableAcceptanceAndReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await RuntimeHarness.ReadAsync<OperationStatus>(response);
        Assert.Equal("op-1", status.Id);
        Assert.Equal(OperationKinds.Launch, status.Kind);
        Assert.Equal("qwen", status.Target);
        Assert.Equal(OperationStages.Admitted, status.Stage);
        Assert.Equal(OperationState.Running, status.State);

        // The reservation was taken before the 202 and persisted with the operation.
        var reservation = Assert.Single(h.Reservations.List());
        Assert.Equal("op-1", reservation.OperationId);
        Assert.Equal(reservation, h.Log.ReservationOf("op-1"));
        Assert.Equal(36, h.Reservations.Requests.Single().Admission.BudgetGiB);

        await h.Operations.WhenIdleAsync();
        var launch = Assert.Single(h.Driver.Launches);
        Assert.Equal(LaunchMode.EnsureStarted, launch.Mode);
        Assert.Equal(reservation.HostPort, launch.HostPort);
        Assert.Equal(ModelState.Starting, h.Driver.Instance("qwen")!.State);

        // Held until Ready: the operation is still running at "started" and the reservation is still there.
        Assert.Equal(OperationStages.Started, h.Log.Find("op-1")!.Stage);
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
        Assert.Single(h.Reservations.List());
        Assert.Equal(
            [OperationStages.Accepted, OperationStages.Admitted, OperationStages.Pulling, OperationStages.Pulled, OperationStages.Created, OperationStages.Started],
            h.Log.StagesOf("op-1"));
    }

    [Fact]
    public async Task Launch_SameIdSamePayload_ReturnsStoredStatusWithoutRepeatingWork()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");

        var first = await RuntimeHarness.ReadAsync<OperationStatus>(await h.PostLaunchAsync("qwen", request));
        await h.Operations.WhenIdleAsync();
        var second = await h.PostLaunchAsync("qwen", request);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var again = await RuntimeHarness.ReadAsync<OperationStatus>(second);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.PayloadDigest, again.PayloadDigest);
        Assert.Equal(OperationStages.Started, again.Stage); // the stored, current status
        Assert.Single(h.Driver.Launches);
        Assert.Single(h.Reservations.Requests);
    }

    [Fact]
    public async Task Launch_SameIdDifferentPayload_Is409OperationIdConflict()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");
        await h.PostLaunchAsync("qwen", request);
        await h.Operations.WhenIdleAsync();

        var response = await h.PostLaunchAsync("qwen", request with { Admission = request.Admission with { BudgetGiB = 40 } });

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.OperationIdConflict);
        Assert.Single(h.Driver.Launches);
    }

    [Fact]
    public async Task Launch_SameIdOtherModel_Is409OperationIdConflict()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");
        await h.PostLaunchAsync("qwen", request);
        await h.Operations.WhenIdleAsync();

        var response = await h.PostLaunchAsync("gemma", request);

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.OperationIdConflict);
    }

    [Fact]
    public async Task Launch_ExpiredEnvelope_Is410AndNothingIsReservedOrRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var now = h.Time.GetUtcNow();

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-old") with { IssuedAt = now.AddHours(-25), ExpiresAt = now.AddHours(-1) });

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Gone, ErrorCodes.OperationExpired);
        Assert.Null(h.Log.Find("op-old"));
        Assert.Empty(h.Reservations.Requests);
        Assert.Empty(h.Driver.Launches);
    }

    [Fact]
    public async Task Launch_IssuedTooFarInTheFuture_Is400ClockSkew()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var now = h.Time.GetUtcNow();

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1") with { IssuedAt = now.AddMinutes(6), ExpiresAt = now.AddHours(24) });

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ClockSkew);
        Assert.Empty(h.Reservations.Requests);
    }

    [Fact]
    public async Task Launch_WithoutMasterHeader_Is400InvalidRequest()
    {
        await using var h = await RuntimeHarness.StartAsync();

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"), sendMaster: false);

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Launch_MalformedBody_Is400InvalidRequest()
    {
        await using var h = await RuntimeHarness.StartAsync();
        using var content = new StringContent("""{"operationId":"op-1"}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/v1/runtimes/qwen/launch") { Content = content };
        request.Headers.Add(Protocol.MasterHeader, RuntimeHarness.Master.ToHeaderValue());

        var response = await h.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Launch_StateUnavailable_Is503AndTheReservationIsReleased()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Log.FailAccepts = true;

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.StateUnavailable);
        Assert.Empty(h.Reservations.List());
        Assert.Contains("op-1", h.Reservations.Released);
        Assert.Empty(h.Driver.Launches);
    }

    // ---- fencing (design §4.2) ----

    [Fact]
    public async Task Launch_StaleMaster_Is409AndNothingIsRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Fencing.AcceptedTriple = RuntimeHarness.Master with { Epoch = 9 };

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.StaleMaster);
        Assert.Null(h.Log.Find("op-1"));
        Assert.Empty(h.Reservations.Requests);
    }

    [Fact]
    public async Task Launch_CompetingMaster_Is409()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Fencing.AcceptedTriple = RuntimeHarness.Master with { BootId = "other-boot" };

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.CompetingMaster);
        Assert.Null(h.Log.Find("op-1"));
    }

    [Fact]
    public async Task Launch_SupersededBeforeCreate_FailsWithSupersededAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostLaunchAsync("qwen", h.Launch("op-1"))).StatusCode);
        await h.Driver.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        h.Fencing.AcceptedTriple = RuntimeHarness.Master with { Epoch = RuntimeHarness.Master.Epoch + 1 }; // a newer master took over
        h.Driver.PullGate.SetResult();
        await h.Operations.WhenIdleAsync();

        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.Superseded, status.Error!.Code);
        Assert.Null(h.Driver.Instance("qwen"));
        Assert.Empty(h.Reservations.List());
        Assert.Contains("op-1", h.Reservations.Released);
    }

    // ---- reservations and admission (design §4.8) ----

    [Fact]
    public async Task Launch_WhileAnotherStartHoldsTheNode_Is409StartConflictAndNotRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostLaunchAsync("qwen", h.Launch("op-1"))).StatusCode);
        await h.Operations.WhenIdleAsync();

        var response = await h.PostLaunchAsync("gemma", h.Launch("op-2"));

        var error = await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.StartConflict);
        Assert.Equal("qwen", error.ReadDetails<StartConflictDetails>()!.StartingModelId);
        Assert.Null(h.Log.Find("op-2"));
    }

    [Fact]
    public async Task Launch_AdmissionRefused_Is409WithDetails()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Reservations.RefuseAdmission = true;

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        var error = await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.AdmissionRefused);
        Assert.Equal(36, error.ReadDetails<AdmissionRefusedDetails>()!.NeedGiB);
        Assert.Null(h.Log.Find("op-1"));
        Assert.Empty(h.Driver.Launches);
    }

    [Fact]
    public async Task Launch_SlowPull_RechecksAdmissionBeforeCreate_AndARefusalFailsTheOperation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.OnPull = () => h.Time.Advance(TimeSpan.FromSeconds(45));
        h.Reservations.RefuseRecheck = true;

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(1, h.Reservations.RecheckCalls);
        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.AdmissionRefused, status.Error!.Code);
        Assert.Null(h.Driver.Instance("qwen"));
        Assert.Empty(h.Reservations.List());
    }

    [Fact]
    public async Task Launch_FastPull_DoesNotRecheckAdmission()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.OnPull = () => h.Time.Advance(TimeSpan.FromSeconds(5));

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(0, h.Reservations.RecheckCalls);
        Assert.Equal(ModelState.Starting, h.Driver.Instance("qwen")!.State);
    }

    // ---- launch modes (design §4.3 "mode") ----

    [Fact]
    public async Task EnsureStarted_ExistingReadyInstance_IsReturnedAsItIsWhateverItsConfiguration()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var existing = h.Driver.Seed("qwen", ModelState.Ready, configHash: "old-hash");

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await RuntimeHarness.ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(existing.InstanceId, status.ReadResult<RuntimeStatus>()!.InstanceId);
        Assert.Empty(h.Driver.Launches);
        Assert.Empty(h.Reservations.Requests); // nothing to start, nothing to reserve
        Assert.Equal("old-hash", h.Driver.Instance("qwen")!.ConfigHash);
    }

    [Fact]
    public async Task EnsureStarted_ExistingStartingInstance_IsNotRecreatedAndWaitsForReady()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var existing = h.Driver.Seed("qwen", ModelState.Starting);

        var status = await RuntimeHarness.ReadAsync<OperationStatus>(await h.PostLaunchAsync("qwen", h.Launch("op-1")));

        Assert.Equal(OperationState.Running, status.State);
        Assert.Equal(OperationStages.Started, status.Stage);
        Assert.Empty(h.Driver.Launches);
        Assert.Equal(existing.InstanceId, h.Driver.Instance("qwen")!.InstanceId);
    }

    [Fact]
    public async Task EnsureStarted_FailedInstance_IsRemovedAndRecreated()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var failed = h.Driver.Seed("qwen", ModelState.Failed);

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Single(h.Driver.Launches);
        var instance = h.Driver.Instance("qwen")!;
        Assert.NotEqual(failed.InstanceId, instance.InstanceId);
        Assert.Equal(ModelState.Starting, instance.State);
        Assert.Equal(0, h.Reservations.Requests.Single().ReleasedGiB); // a failed instance holds no memory
    }

    [Fact]
    public async Task Replace_AlwaysRecreatesEvenWithAnIdenticalConfiguration_AndReprobesAfterRemoval()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var existing = h.Driver.Seed("qwen", ModelState.Ready, configHash: "9f2c", budgetGiB: "36");

        await h.PostLaunchAsync("qwen", h.Launch("op-1", LaunchMode.Replace));
        await h.Operations.WhenIdleAsync();

        var instance = h.Driver.Instance("qwen")!;
        Assert.NotEqual(existing.InstanceId, instance.InstanceId);
        Assert.Equal(LaunchMode.Replace, h.Driver.Launches.Single().Mode);
        Assert.Equal(36, h.Reservations.Requests.Single().ReleasedGiB); // pre-removal figure from the budget label
        Assert.Equal(1, h.Reservations.RecheckCalls);                    // the measured figure after removal
        Assert.Equal(
            [OperationStages.Accepted, OperationStages.Admitted, OperationStages.Pulling, OperationStages.Pulled, OperationStages.Stopping,
             OperationStages.Removed, OperationStages.Created, OperationStages.Started],
            h.Log.StagesOf("op-1"));
    }

    [Fact]
    public async Task Replace_NewOperationIdAlwaysRecreatesAgain()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        h.Driver.InitialState = ModelState.Ready;

        await h.PostLaunchAsync("qwen", h.Launch("op-1", LaunchMode.Replace));
        await h.Operations.WhenIdleAsync();
        string first = h.Driver.Instance("qwen")!.InstanceId!;
        await h.PostLaunchAsync("qwen", h.Launch("op-2", LaunchMode.Replace));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(2, h.Driver.Launches.Count);
        Assert.NotEqual(first, h.Driver.Instance("qwen")!.InstanceId);
        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-2")!.State);
    }

    [Fact]
    public async Task Launch_InstanceReadyAtOnce_SucceedsAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.InitialState = ModelState.Ready;

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(OperationStages.Ready, h.Log.StagesOf("op-1")[^1]);
        Assert.Equal("qwen", status.ReadResult<RuntimeStatus>()!.ModelId);
        Assert.Empty(h.Reservations.List());
    }

    [Theory]
    [InlineData("mirbadllm.model")]
    [InlineData("mirbadllm.node")]
    [InlineData("mirbadllm.operation")]
    public async Task Launch_SpecWithReservedLabel_Is422SpecRejected(string label)
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");
        request = request with { Spec = request.Spec with { Labels = new Dictionary<string, string> { [label] = "x" } } };

        var response = await h.PostLaunchAsync("qwen", request);

        var error = await RuntimeHarness.AssertErrorAsync(response, (HttpStatusCode)422, ErrorCodes.SpecRejected);
        Assert.Contains(label, error.ReadDetails<SpecRejectedDetails>()!.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Reservations.Requests);
        Assert.Null(h.Log.Find("op-1"));
    }

    [Fact]
    public async Task Launch_SpecTheDriverRefuses_Is422BeforeAnythingIsReservedOrRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.RejectSpecReason = "image 'x' with digest 'sha256:…' is not approved on this node";

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        var error = await RuntimeHarness.AssertErrorAsync(response, (HttpStatusCode)422, ErrorCodes.SpecRejected);
        Assert.Contains("not approved", error.ReadDetails<SpecRejectedDetails>()!.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Reservations.Requests);
        Assert.Null(h.Log.Find("op-1"));
        Assert.Empty(h.Driver.Launches);
    }

    [Fact]
    public async Task Launch_SpecWithForeignMount_Is422SpecRejected()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");
        request = request with { Spec = request.Spec with { Mounts = [new MountSpec("models", null, "/etc", true)] } };

        await RuntimeHarness.AssertErrorAsync(await h.PostLaunchAsync("qwen", request), (HttpStatusCode)422, ErrorCodes.SpecRejected);
    }

    [Fact]
    public async Task Launch_ProcessSpecWithoutProcessDriver_Is422SpecRejected()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var request = h.Launch("op-1");
        request = request with { Spec = request.Spec with { Kind = LaunchDelivery.Process, Image = null, Executable = "llama-server" } };

        var response = await h.PostLaunchAsync("qwen", request);

        // MN-239 H3: this refusal is the one an operator meets on a node with Docker off and no process delivery
        // (D131 (7), operations-p3p4 §11.1 (3)), and it had no assertion on its wording at all. It now carries the
        // "Launch spec rejected:" prefix the drivers' own validators carry, with the bare reason in the details, and
        // names the delivery that has no driver.
        var error = await RuntimeHarness.AssertErrorAsync(response, (HttpStatusCode)422, ErrorCodes.SpecRejected);
        Assert.Equal("Launch spec rejected: this node has no runtime driver for delivery 'process'.", error.Message);
        Assert.Equal("this node has no runtime driver for delivery 'process'", error.ReadDetails<SpecRejectedDetails>()!.Reason);
        Assert.Empty(h.Reservations.Requests);
        Assert.Null(h.Log.Find("op-1"));
    }

    [Fact]
    public async Task Launch_DriverFailure_FailsTheOperationAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.LaunchFailure = new AgentException(422, new AgentError { Code = ErrorCodes.SpecRejected, Message = "digest not approved" });

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.SpecRejected, status.Error!.Code);
        Assert.Empty(h.Reservations.List());
    }

    [Fact]
    public async Task Launch_DockerUnreachable_FailsWithDockerUnavailable()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.LaunchFailure = new HttpRequestException("connection refused");

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(ErrorCodes.DockerUnavailable, h.Log.Find("op-1")!.Error!.Code);
    }

    [Fact]
    public async Task Launch_DockerUnreachableBeforeAcceptance_Is503AndNothingIsRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.ListFailure = new HttpRequestException("no docker");

        var response = await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DockerUnavailable);
        Assert.Null(h.Log.Find("op-1"));
        Assert.Empty(h.Reservations.Requests);
    }

    // ---- stop ----

    [Fact]
    public async Task Stop_RemovesTheInstance_AndPublishesItsRemoval()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);

        var response = await h.PostStopAsync("qwen", h.Stop("stop-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(h.Driver.Instance("qwen"));
        var status = h.Log.Find("stop-1")!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal([OperationStages.Accepted, OperationStages.Stopping, OperationStages.Removed], h.Log.StagesOf("stop-1"));
        var removal = h.Events.Of<RuntimeEvent>()[^1];
        Assert.Equal("qwen", removal.ModelId);
        Assert.Null(removal.Runtime);
    }

    [Fact]
    public async Task Stop_WithoutInstance_Succeeds()
    {
        await using var h = await RuntimeHarness.StartAsync();

        await h.PostStopAsync("qwen", h.Stop("stop-1"));
        await h.Operations.WhenIdleAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("stop-1")!.State);
        Assert.Equal(0, h.Driver.Stops);
    }

    [Fact]
    public async Task Stop_BySupersededMaster_FailsBeforeTheDestructiveStep()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        h.Driver.BeforeStop = () => h.Fencing.AcceptedTriple = RuntimeHarness.Master with { Epoch = 99 };

        await h.PostStopAsync("qwen", h.Stop("stop-1"));
        await h.Operations.WhenIdleAsync();

        var status = h.Log.Find("stop-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Equal(ErrorCodes.Superseded, status.Error!.Code);
        Assert.NotNull(h.Driver.Instance("qwen"));
    }

    [Fact]
    public async Task Stop_Duplicate_ReturnsTheStoredStatus()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        var request = h.Stop("stop-1");

        await h.PostStopAsync("qwen", request);
        await h.Operations.WhenIdleAsync();
        var again = await RuntimeHarness.ReadAsync<OperationStatus>(await h.PostStopAsync("qwen", request));

        Assert.Equal(OperationState.Succeeded, again.State);
        Assert.Equal(1, h.Driver.Stops);
    }

    // ---- cancel (design §4.4 item 7) ----

    [Fact]
    public async Task Cancel_LaunchDuringPull_CancelsItAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Driver.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var response = await h.PostCancelAsync("op-1", h.CancelRequest("cancel-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancel = await RuntimeHarness.ReadAsync<OperationStatus>(response);
        Assert.Equal(OperationKinds.OperationCancel, cancel.Kind);
        Assert.Equal(OperationState.Succeeded, cancel.State);
        Assert.Equal(OperationState.Cancelled, cancel.ReadResult<OperationStatus>()!.State);
        Assert.Equal(OperationState.Cancelled, h.Log.Find("op-1")!.State);
        Assert.Empty(h.Reservations.List());
        Assert.Null(h.Driver.Instance("qwen"));
        Assert.Equal(1, h.Events.Of<ReservationEvent>().Count(e => e.Released));
    }

    [Fact]
    public async Task Cancel_LaunchWhoseInstanceExists_Is409InvalidStateAndNotRecorded()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        var response = await h.PostCancelAsync("op-1", h.CancelRequest("cancel-1"));

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        Assert.Null(h.Log.Find("cancel-1"));
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
    }

    [Fact]
    public async Task Cancel_UnknownOperation_Is404()
    {
        await using var h = await RuntimeHarness.StartAsync();

        await RuntimeHarness.AssertErrorAsync(await h.PostCancelAsync("nope", h.CancelRequest("cancel-1")), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Cancel_TerminalOperation_SucceedsWithItsFinalState()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostStopAsync("qwen", h.Stop("stop-1"));
        await h.Operations.WhenIdleAsync();

        var cancel = await RuntimeHarness.ReadAsync<OperationStatus>(await h.PostCancelAsync("stop-1", h.CancelRequest("cancel-1")));

        Assert.Equal(OperationState.Succeeded, cancel.State);
        Assert.Equal(OperationState.Succeeded, cancel.ReadResult<OperationStatus>()!.State);
    }

    [Fact]
    public async Task Cancel_ActiveStop_Is409InvalidState()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        h.Driver.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.PostStopAsync("qwen", h.Stop("stop-1"));

        var response = await h.PostCancelAsync("stop-1", h.CancelRequest("cancel-1"));
        h.Driver.StopGate.SetResult();
        await h.Operations.WhenIdleAsync();

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Cancel_RepeatedCancelId_ReturnsTheStoredCancel()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Driver.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var request = h.CancelRequest("cancel-1");

        await h.PostCancelAsync("op-1", request);
        var again = await h.PostCancelAsync("op-1", request);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(OperationState.Succeeded, (await RuntimeHarness.ReadAsync<OperationStatus>(again)).State);
    }

    [Fact]
    public async Task Cancel_AdmittedLaunchNotExecutingHere_IsCancelledAndItsReservationReleased()
    {
        await using var h = await RuntimeHarness.StartAsync();
        // Accepted by a previous agent boot (rebuilt reservation, not resumed yet).
        await h.Log.SeedAsync("op-1", OperationKinds.Launch, "qwen", OperationStages.Admitted, h.Time.GetUtcNow());
        await h.Reservations.RebuildAsync([RuntimeHarness.ReservationFor("op-1", "qwen", h.Time.GetUtcNow())], TestContext.Current.CancellationToken);

        var cancel = await RuntimeHarness.ReadAsync<OperationStatus>(await h.PostCancelAsync("op-1", h.CancelRequest("cancel-1")));

        Assert.Equal(OperationState.Cancelled, cancel.ReadResult<OperationStatus>()!.State);
        Assert.Empty(h.Reservations.List());
    }

    // ---- reads ----

    [Fact]
    public async Task Operations_GetAndList()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.PostStopAsync("gemma", h.Stop("stop-1"));
        await h.Operations.WhenIdleAsync();
        var ct = TestContext.Current.CancellationToken;

        var one = await h.Client.GetFromJsonAsync<OperationStatus>("/agent/v1/operations/op-1", AgentJson.Options, ct);
        var active = await h.Client.GetFromJsonAsync<List<OperationStatus>>("/agent/v1/operations?state=active", AgentJson.Options, ct);
        var all = await h.Client.GetFromJsonAsync<List<OperationStatus>>("/agent/v1/operations", AgentJson.Options, ct);

        Assert.Equal("op-1", one!.Id);
        Assert.Equal(["op-1"], active!.Select(o => o.Id));
        Assert.Equal(["op-1", "stop-1"], all!.Select(o => o.Id).Order(StringComparer.Ordinal));
        await RuntimeHarness.AssertErrorAsync(await h.Client.GetAsync("/agent/v1/operations/nope", ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await RuntimeHarness.AssertErrorAsync(await h.Client.GetAsync("/agent/v1/operations?state=weird", ct), HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Runtimes_ListGetLaunchConfigAndLogs()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        h.Driver.Seed("cand", ModelState.Starting, tuning: true);
        var ct = TestContext.Current.CancellationToken;

        var all = await h.Client.GetFromJsonAsync<List<RuntimeStatus>>("/agent/v1/runtimes", AgentJson.Options, ct);
        var tuning = await h.Client.GetFromJsonAsync<List<RuntimeStatus>>("/agent/v1/runtimes?label=tuning", AgentJson.Options, ct);
        var one = await h.Client.GetFromJsonAsync<RuntimeStatus>("/agent/v1/runtimes/qwen", AgentJson.Options, ct);
        var config = await h.Client.GetFromJsonAsync<LaunchConfig>("/agent/v1/runtimes/qwen/launch-config", AgentJson.Options, ct);
        string logs = await h.Client.GetStringAsync("/agent/v1/runtimes/qwen/logs?tail=2&follow=false", ct);

        Assert.Equal(2, all!.Count);
        Assert.Equal(["cand"], tuning!.Select(r => r.ModelId));
        Assert.Equal(ModelState.Ready, one!.State);
        Assert.Equal("local/vllm:test", config!.Image);
        Assert.Equal("data: line 1\n\ndata: line 2\n\n", logs);
        await RuntimeHarness.AssertErrorAsync(await h.Client.GetAsync("/agent/v1/runtimes/none", ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await RuntimeHarness.AssertErrorAsync(await h.Client.GetAsync("/agent/v1/runtimes/none/launch-config", ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await RuntimeHarness.AssertErrorAsync(await h.Client.GetAsync("/agent/v1/runtimes/none/logs", ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Runtimes_DockerUnreachable_Is503DockerUnavailable()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.ListFailure = new HttpRequestException("no docker");

        var response = await h.Client.GetAsync("/agent/v1/runtimes", TestContext.Current.CancellationToken);

        await RuntimeHarness.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DockerUnavailable);
    }

    // ---- events (design §4.7) ----

    [Fact]
    public async Task Launch_EventsArePublishedInOrderWithIncreasingSeq()
    {
        await using var h = await RuntimeHarness.StartAsync();

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        var events = h.Events.Published;
        Assert.True(events.Select(e => e.Seq).SequenceEqual(Enumerable.Range(1, events.Count).Select(i => (long)i)));
        Assert.IsType<ReservationEvent>(events[0]);
        Assert.False(((ReservationEvent)events[0]).Released);
        Assert.Equal(
            [OperationStages.Accepted, OperationStages.Admitted, OperationStages.Pulling, OperationStages.Pulled, OperationStages.Created, OperationStages.Started],
            events.OfType<OperationEvent>().Select(e => e.Operation.Stage));
        var runtime = Assert.Single(events.OfType<RuntimeEvent>());
        Assert.Equal(ModelState.Starting, runtime.Runtime!.State);
        Assert.IsType<RuntimeEvent>(events[^1]); // the instance is reported after the "started" stage
    }

    // ---- composition ----

    [Fact]
    public void AddAgentRuntimes_ResolvesEveryServiceAndTheWatcherAsAHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var fakes = new RuntimeFakes();
        fakes.Register(services);
        services.AddAgentRuntimes();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<RuntimeOperations>());
        Assert.NotNull(provider.GetRequiredService<OperationCancellation>());
        Assert.Same(provider.GetRequiredService<RuntimeOperations>(), Assert.Single(provider.GetServices<IOperationCanceller>()));
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is RuntimeWatcher);
    }
}

// ======================================================================== harness and fakes

/// <summary>The four collaborators and the driver, as hand-written fakes.</summary>
internal sealed class RuntimeFakes
{
    public ManualTimeProvider Time { get; } = new();

    public FakeOperationLog Log { get; }

    public FakeFencingState Fencing { get; } = new() { AcceptedTriple = RuntimeHarness.Master };

    public FakeReservationManager Reservations { get; }

    public FakeEventBus Events { get; }

    public FakeRuntimeDriver Driver { get; } = new();

    public RuntimeFakes()
    {
        Log = new FakeOperationLog(Time);
        Events = new FakeEventBus(Time);
        Reservations = new FakeReservationManager(Time, Events);
    }

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IOperationLog>(Log);
        services.AddSingleton<IFencingState>(Fencing);
        services.AddSingleton<IReservationManager>(Reservations);
        services.AddSingleton<IEventBus>(Events);
        services.AddSingleton<IRuntimeDriver>(Driver);
    }
}

/// <summary>An in-process agent with only the runtime and operation routes (no watcher running on its own).</summary>
internal sealed class RuntimeHarness : IAsyncDisposable
{
    public static readonly MasterTriple Master = new("master-a", 3, "boot-a");

    private RuntimeHarness(WebApplication app, RuntimeFakes fakes)
    {
        App = app;
        Fakes = fakes;
        Client = app.GetTestClient();
        Operations = app.Services.GetRequiredService<RuntimeOperations>();
        Watcher = app.Services.GetRequiredService<RuntimeWatcher>();
        Board = app.Services.GetRequiredService<RuntimeStateBoard>();
    }

    public WebApplication App { get; }

    public RuntimeFakes Fakes { get; }

    public HttpClient Client { get; }

    public RuntimeOperations Operations { get; }

    public RuntimeWatcher Watcher { get; }

    public RuntimeStateBoard Board { get; }

    public ManualTimeProvider Time => Fakes.Time;

    public FakeOperationLog Log => Fakes.Log;

    public FakeFencingState Fencing => Fakes.Fencing;

    public FakeReservationManager Reservations => Fakes.Reservations;

    public FakeEventBus Events => Fakes.Events;

    public FakeRuntimeDriver Driver => Fakes.Driver;

    public static async Task<RuntimeHarness> StartAsync()
    {
        var fakes = new RuntimeFakes();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        fakes.Register(builder.Services);
        var options = new AgentRuntimeOptions { WatchInterval = TimeSpan.FromHours(1) };
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<MutationGate>();
        builder.Services.AddSingleton<RuntimeStateBoard>();
        builder.Services.AddSingleton<RuntimeOperations>();
        builder.Services.AddSingleton<IOperationCanceller>(sp => sp.GetRequiredService<RuntimeOperations>());
        builder.Services.AddSingleton<OperationCancellation>();
        builder.Services.AddSingleton<RuntimeWatcher>(); // polled explicitly by the tests
        var app = builder.Build();
        app.MapAgentRuntimeEndpoints();
        app.MapAgentOperationEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new RuntimeHarness(app, fakes);
    }

    public async ValueTask DisposeAsync()
    {
        Driver.PullGate?.TrySetResult();
        Driver.StopGate?.TrySetResult();
        await Operations.WhenIdleAsync();
        Client.Dispose();
        await App.DisposeAsync();
    }

    public LaunchRequest Launch(string operationId, LaunchMode mode = LaunchMode.EnsureStarted)
    {
        var now = Time.GetUtcNow();
        return new LaunchRequest
        {
            OperationId = operationId,
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Mode = mode,
            Spec = new LaunchSpec
            {
                Kind = LaunchDelivery.Container,
                Image = "local/vllm:test",
                Argv = ["--model", "/models/qwen"],
                Mounts =
                [
                    new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true),
                    new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false),
                ],
                Labels = new Dictionary<string, string> { [ContainerLabels.ConfigHash] = "9f2c", [ContainerLabels.BudgetGiB] = "36" },
            },
            Admission = new AdmissionRequest { BudgetGiB = 36, ReserveGiB = 8 },
            ExpectedConfigHash = "9f2c",
        };
    }

    public StopRequest Stop(string operationId)
    {
        var now = Time.GetUtcNow();
        return new StopRequest { OperationId = operationId, IssuedAt = now, ExpiresAt = now + OperationRetention.EnvelopeLifetime };
    }

    public OperationCancelRequest CancelRequest(string operationId)
    {
        var now = Time.GetUtcNow();
        return new OperationCancelRequest { OperationId = operationId, IssuedAt = now, ExpiresAt = now + OperationRetention.EnvelopeLifetime };
    }

    public static ReservationInfo ReservationFor(string operationId, string modelId, DateTimeOffset at) =>
        new() { OperationId = operationId, ModelId = modelId, BudgetGiB = 36, HostPort = 18100, CreatedAt = at };

    public Task<HttpResponseMessage> PostLaunchAsync(string modelId, LaunchRequest request, bool sendMaster = true) =>
        PostAsync("/agent/v1/runtimes/" + modelId + "/launch", request, sendMaster);

    public Task<HttpResponseMessage> PostStopAsync(string modelId, StopRequest request) =>
        PostAsync("/agent/v1/runtimes/" + modelId + "/stop", request, sendMaster: true);

    public Task<HttpResponseMessage> PostCancelAsync(string targetId, OperationCancelRequest request) =>
        PostAsync("/agent/v1/operations/" + targetId + "/cancel", request, sendMaster: true);

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, bool sendMaster)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: AgentJson.Options),
        };
        if (sendMaster)
        {
            request.Headers.Add(Protocol.MasterHeader, Master.ToHeaderValue());
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(AgentJson.Options, TestContext.Current.CancellationToken);
        return value ?? throw new InvalidOperationException("Empty body.");
    }

    public static async Task<AgentError> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await ReadAsync<AgentErrorResponse>(response);
        Assert.Equal(code, body.Error.Code);
        return body.Error;
    }
}

/// <summary>In-memory <see cref="IOperationLog"/> with the §4.4 acceptance rules (digest over the raw body).</summary>
internal sealed class FakeOperationLog(TimeProvider time) : IOperationLog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public bool FailAccepts { get; set; }

    public int TornLinesIgnored => 0;

    public Task<OperationAcceptance> AcceptAsync(OperationSubmission submission, CancellationToken ct = default)
    {
        string digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(submission.Payload.GetRawText())));
        lock (_gate)
        {
            if (_entries.TryGetValue(submission.OperationId, out var known))
            {
                bool same = known.Status.Kind == submission.Kind && known.Status.Target == submission.Target && known.Status.PayloadDigest == digest;
                return Task.FromResult(same
                    ? new OperationAcceptance(OperationAcceptOutcome.Duplicate, known.Status)
                    : new OperationAcceptance(OperationAcceptOutcome.Conflict, null));
            }

            var now = time.GetUtcNow();
            if (submission.ExpiresAt <= now)
            {
                return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Expired, null));
            }

            if (submission.IssuedAt - now > OperationRetention.MaxIssuedAtFutureSkew)
            {
                return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.IssuedInFuture, null));
            }

            if (FailAccepts)
            {
                throw new IOException("disk full");
            }

            var status = new OperationStatus
            {
                Id = submission.OperationId,
                Kind = submission.Kind,
                Target = submission.Target,
                PayloadDigest = digest,
                State = OperationState.Accepted,
                Stage = OperationStages.Accepted,
                IssuedAt = submission.IssuedAt,
                ExpiresAt = submission.ExpiresAt,
                StartedAt = now,
            };
            _entries[submission.OperationId] = new Entry(status, submission.Master) { Stages = [OperationStages.Accepted] };
            _order.Add(submission.OperationId);
            return Task.FromResult(new OperationAcceptance(OperationAcceptOutcome.Accepted, status));
        }
    }

    public Task SeedAsync(string id, string kind, string target, string stage, DateTimeOffset now) =>
        AcceptAsync(new OperationSubmission
        {
            OperationId = id,
            Kind = kind,
            Target = target,
            IssuedAt = now,
            ExpiresAt = now.AddHours(24),
            Master = RuntimeHarness.Master,
            Payload = JsonSerializer.SerializeToElement(new { id }),
        }).ContinueWith(_ => RecordStageAsync(id, new OperationStageUpdate { Stage = stage }), TaskScheduler.Default).Unwrap();

    public Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entry = _entries[operationId];
            var status = entry.Status with
            {
                Stage = update.Stage ?? entry.Status.Stage,
                State = update.State ?? (entry.Status.State == OperationState.Accepted ? OperationState.Running : entry.Status.State),
                Progress = update.Progress ?? entry.Status.Progress,
            };
            entry.Status = status;
            entry.Reservation = update.Reservation ?? entry.Reservation;
            if (update.Stage is not null)
            {
                entry.Stages.Add(update.Stage);
            }

            return Task.FromResult(status);
        }
    }

    public Task<OperationStatus> CompleteAsync(string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entry = _entries[operationId];
            if (RuntimeOperations.IsTerminal(entry.Status.State))
            {
                return Task.FromResult(entry.Status);
            }

            entry.Status = entry.Status with
            {
                State = state,
                Stage = OperationStages.Completed,
                Error = failure,
                Result = result,
                EndedAt = time.GetUtcNow(),
            };
            return Task.FromResult(entry.Status);
        }
    }

    public OperationStatus? Find(string operationId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(operationId, out var entry) ? entry.Status : null;
        }
    }

    public IReadOnlyList<OperationStatus> ListActive()
    {
        lock (_gate)
        {
            return [.. _order.Select(id => _entries[id].Status).Where(s => !RuntimeOperations.IsTerminal(s.State))];
        }
    }

    public IReadOnlyList<OperationStatus> ListRecent(int max)
    {
        lock (_gate)
        {
            return [.. _order.AsEnumerable().Reverse().Select(id => _entries[id].Status).Where(s => RuntimeOperations.IsTerminal(s.State)).Take(max)];
        }
    }

    public IReadOnlyList<RecoverableOperation> ListRecoverable()
    {
        lock (_gate)
        {
            return [.. _order.Select(id => _entries[id]).Where(e => !RuntimeOperations.IsTerminal(e.Status.State))
                .Select(e => new RecoverableOperation { Status = e.Status, Master = e.Master, Reservation = e.Reservation })];
        }
    }

    public Task CompactAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Stage history (completion excluded).</summary>
    public IReadOnlyList<string> StagesOf(string operationId)
    {
        lock (_gate)
        {
            return [.. _entries[operationId].Stages];
        }
    }

    public ReservationInfo? ReservationOf(string operationId)
    {
        lock (_gate)
        {
            return _entries[operationId].Reservation;
        }
    }

    private sealed class Entry(OperationStatus status, MasterTriple master)
    {
        public OperationStatus Status { get; set; } = status;

        public MasterTriple Master { get; } = master;

        public ReservationInfo? Reservation { get; set; }

        public List<string> Stages { get; init; } = [];
    }
}

/// <summary><see cref="IFencingState"/> with the §4.2 decision table over a settable accepted triple.</summary>
internal sealed class FakeFencingState : IFencingState
{
    private readonly Lock _gate = new();
    private MasterTriple? _accepted;

    public MasterTriple? AcceptedTriple
    {
        get
        {
            lock (_gate)
            {
                return _accepted;
            }
        }

        set
        {
            lock (_gate)
            {
                _accepted = value;
            }
        }
    }

    public MasterTriple? Accepted => AcceptedTriple;

    public Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_accepted is null || presented.Epoch > _accepted.Epoch)
            {
                _accepted = presented;
                return Task.FromResult(new FencingCheck(FencingDecision.Accepted, presented));
            }

            if (presented.Epoch < _accepted.Epoch)
            {
                return Task.FromResult(new FencingCheck(FencingDecision.StaleMaster, _accepted));
            }

            return Task.FromResult(presented == _accepted
                ? new FencingCheck(FencingDecision.Accepted, presented)
                : new FencingCheck(FencingDecision.CompetingMaster, _accepted));
        }
    }

    public bool IsCurrent(MasterTriple operationMaster) => AcceptedTriple == operationMaster;
}

/// <summary>
/// Unified-node <see cref="IReservationManager"/>: one start at a time, switchable admission refusals. Like the real
/// manager it is the only publisher of <see cref="ReservationEvent"/>s (W2 review MN-092).
/// </summary>
internal sealed class FakeReservationManager(TimeProvider time, IEventBus? events = null) : IReservationManager
{
    private readonly Lock _gate = new();
    private readonly List<ReservationInfo> _held = [];
    private int _nextPort = 18000;
    private int _rechecks;

    public bool RefuseAdmission { get; set; }

    public bool RefuseRecheck { get; set; }

    public int RecheckCalls => Volatile.Read(ref _rechecks);

    public List<StartReservationRequest> Requests { get; } = [];

    public List<string> Released { get; } = [];

    public Task<ReservationResult> TryReserveAsync(StartReservationRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Requests.Add(request);
            if (_held.FirstOrDefault() is { } other)
            {
                return Task.FromResult(new ReservationResult
                {
                    Outcome = ReservationOutcome.StartConflict,
                    Error = new AgentError
                    {
                        Code = ErrorCodes.StartConflict,
                        Message = "Model '" + other.ModelId + "' is still starting on node dgx.",
                        Details = AgentJson.ToElement(new StartConflictDetails { StartingModelId = other.ModelId }),
                    },
                });
            }

            if (RefuseAdmission)
            {
                return Task.FromResult(new ReservationResult
                {
                    Outcome = ReservationOutcome.AdmissionRefused,
                    Error = new AgentError
                    {
                        Code = ErrorCodes.AdmissionRefused,
                        Message = "Not enough memory on node dgx.",
                        Details = AgentJson.ToElement(new AdmissionRefusedDetails { NeedGiB = request.Admission.BudgetGiB, FreeGiB = 10, AvailableGiB = 18, ReserveGiB = 8 }),
                    },
                });
            }

            var reservation = new ReservationInfo
            {
                OperationId = request.OperationId,
                ModelId = request.ModelId,
                BudgetGiB = request.Admission.BudgetGiB,
                HostPort = _nextPort++,
                CreatedAt = time.GetUtcNow(),
            };
            _held.Add(reservation);
            events?.Publish(new ReservationEvent { Reservation = reservation, Released = false });
            return Task.FromResult(new ReservationResult { Outcome = ReservationOutcome.Reserved, Reservation = reservation });
        }
    }

    public Task<ReservationResult> RecheckAsync(string operationId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _rechecks);
        lock (_gate)
        {
            var held = _held.FirstOrDefault(r => r.OperationId == operationId);
            return Task.FromResult(RefuseRecheck || held is null
                ? new ReservationResult
                {
                    Outcome = ReservationOutcome.AdmissionRefused,
                    Error = new AgentError { Code = ErrorCodes.AdmissionRefused, Message = "Not enough memory on node dgx after the pull." },
                }
                : new ReservationResult { Outcome = ReservationOutcome.Reserved, Reservation = held });
        }
    }

    public Task ReleaseAsync(string operationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var held in _held.Where(r => r.OperationId == operationId).ToList())
            {
                _held.Remove(held);
                Released.Add(operationId);
                events?.Publish(new ReservationEvent { Reservation = held, Released = true });
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<ReservationInfo> List()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    public Task RebuildAsync(IEnumerable<ReservationInfo> reservations, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _held.AddRange(reservations);
        }

        return Task.CompletedTask;
    }

    public Task<int> AllocatePortAsync(string modelId, string operationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_held.FirstOrDefault(r => r.OperationId == operationId)?.HostPort ?? _nextPort++);
        }
    }
}

/// <summary><see cref="IEventBus"/> that stamps seq under one lock and records what was published.</summary>
internal sealed class FakeEventBus(TimeProvider time) : IEventBus
{
    private readonly Lock _gate = new();
    private readonly List<AgentEvent> _published = [];
    private long _seq;

    public string BootId { get; } = "agent-boot-1";

    public long CurrentSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    public IReadOnlyList<AgentEvent> Published
    {
        get
        {
            lock (_gate)
            {
                return [.. _published];
            }
        }
    }

    public IReadOnlyList<T> Of<T>()
        where T : AgentEvent => [.. Published.OfType<T>()];

    public AgentEvent Publish(AgentEvent evt) => Publish(() => { }, evt);

    public AgentEvent Publish(Action stateChange, AgentEvent evt)
    {
        lock (_gate)
        {
            stateChange();
            var stamped = evt with { BootId = BootId, Seq = ++_seq, At = time.GetUtcNow() };
            _published.Add(stamped);
            return stamped;
        }
    }

    public T Snapshot<T>(Func<long, T> build)
    {
        lock (_gate)
        {
            return build(_seq);
        }
    }

    public IEventSubscription Subscribe(string? bootId, long? afterSeq) => throw new NotSupportedException();
}

/// <summary>In-memory container driver honouring the stage-sink protocol (destructive steps are announced first).</summary>
internal sealed class FakeRuntimeDriver : IRuntimeDriver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RuntimeStatus> _instances = new(StringComparer.Ordinal);
    private int _nextInstance;
    private int _stops;
    private Func<Task>? _whileListing;

    public LaunchDelivery Delivery => LaunchDelivery.Container;

    public List<RuntimeLaunch> Launches { get; } = [];

    public int Stops => Volatile.Read(ref _stops);

    public ModelState InitialState { get; set; } = ModelState.Starting;

    public TaskCompletionSource? PullGate { get; set; }

    public TaskCompletionSource PullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? StopGate { get; set; }

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action? OnPull { get; set; }

    public Action? BeforeStop { get; set; }

    /// <summary>Runs once, inside the next <see cref="ListAsync"/>, after the listing was taken (a change it cannot see).</summary>
    public Func<Task>? WhileListing
    {
        get => Volatile.Read(ref _whileListing);
        set => Volatile.Write(ref _whileListing, value);
    }

    /// <summary>Runs once the instance is started, before the launch gets the result (what a watcher could see then).</summary>
    public Func<Task>? AfterStart { get; set; }

    /// <summary>When set, <see cref="CheckSpec"/> refuses every spec with this reason (the driver's own guardrails).</summary>
    public string? RejectSpecReason { get; set; }

    public void CheckSpec(string modelId, LaunchSpec spec)
    {
        if (RejectSpecReason is { } reason)
        {
            throw new AgentException(422, new AgentError
            {
                Code = ErrorCodes.SpecRejected,
                Message = reason,
                Details = AgentJson.ToElement(new SpecRejectedDetails(reason)),
            });
        }
    }

    public Exception? LaunchFailure { get; set; }

    public Exception? ListFailure { get; set; }

    public RuntimeStatus Seed(string modelId, ModelState state, string? configHash = null, string? budgetGiB = null, bool tuning = false, string? operationId = null)
    {
        var labels = new Dictionary<string, string> { [ContainerLabels.Model] = modelId };
        if (budgetGiB is not null)
        {
            labels[ContainerLabels.BudgetGiB] = budgetGiB;
        }

        var instance = new RuntimeStatus
        {
            ModelId = modelId,
            State = state,
            InstanceId = "c-" + Interlocked.Increment(ref _nextInstance),
            HostPort = 17000 + _nextInstance,
            ConfigHash = configHash,
            Tuning = tuning,
            Labels = labels,
            OperationId = operationId,
            Error = state == ModelState.Failed ? "crash loop" : null,
        };
        lock (_gate)
        {
            _instances[modelId] = instance;
        }

        return instance;
    }

    public RuntimeStatus? Instance(string modelId)
    {
        lock (_gate)
        {
            return _instances.GetValueOrDefault(modelId);
        }
    }

    public void SetState(string modelId, ModelState state, string? error = null)
    {
        lock (_gate)
        {
            _instances[modelId] = _instances[modelId] with { State = state, Error = error };
        }
    }

    public void Remove(string modelId)
    {
        lock (_gate)
        {
            _instances.Remove(modelId);
        }
    }

    public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        IReadOnlyList<RuntimeStatus> listing;
        lock (_gate)
        {
            listing = [.. _instances.Values];
        }

        if (Interlocked.Exchange(ref _whileListing, null) is { } whileListing)
        {
            return ListThenAsync(listing, whileListing);
        }

        return Task.FromResult(listing);

        static async Task<IReadOnlyList<RuntimeStatus>> ListThenAsync(IReadOnlyList<RuntimeStatus> listing, Func<Task> action)
        {
            await action();
            return listing;
        }
    }

    public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        return Task.FromResult(Instance(modelId));
    }

    public async Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Launches.Add(launch);
        }

        var existing = Instance(launch.ModelId);
        if (launch.Mode == LaunchMode.EnsureStarted && existing is { State: ModelState.Starting or ModelState.Ready or ModelState.Stopping })
        {
            return new RuntimeLaunchResult(RuntimeLaunchOutcome.AlreadyRunning, existing);
        }

        await stages.StageReachedAsync(OperationStages.Pulling, new OperationProgress { Percent = 0 }, ct);
        PullStarted.TrySetResult();
        OnPull?.Invoke();
        if (PullGate is not null)
        {
            await PullGate.Task.WaitAsync(ct);
        }

        if (LaunchFailure is not null)
        {
            throw LaunchFailure;
        }

        await stages.StageReachedAsync(OperationStages.Pulled, null, ct);

        if (existing is not null)
        {
            if (existing.State != ModelState.Failed)
            {
                await stages.BeforeStepAsync(OperationStages.Stopping, ct);
                await stages.StageReachedAsync(OperationStages.Stopping, null, ct);
            }

            await stages.BeforeStepAsync(OperationStages.Removed, ct);
            Remove(launch.ModelId);
            await stages.StageReachedAsync(OperationStages.Removed, null, ct);
        }

        await stages.BeforeStepAsync(OperationStages.Created, ct);
        var created = Seed(launch.ModelId, ModelState.Stopped, operationId: launch.OperationId) with { HostPort = launch.HostPort };
        await stages.StageReachedAsync(OperationStages.Created, null, CancellationToken.None);
        await stages.BeforeStepAsync(OperationStages.Started, CancellationToken.None);
        var started = created with { State = InitialState };
        lock (_gate)
        {
            _instances[launch.ModelId] = started;
        }

        await stages.StageReachedAsync(OperationStages.Started, null, CancellationToken.None);
        if (AfterStart is not null)
        {
            await AfterStart();
        }

        return new RuntimeLaunchResult(RuntimeLaunchOutcome.Created, started);
    }

    public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

    public async Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stops);
        if (Instance(modelId) is null)
        {
            return;
        }

        StopEntered.TrySetResult();
        if (StopGate is not null)
        {
            await StopGate.Task.WaitAsync(ct);
        }

        BeforeStop?.Invoke();
        await stages.BeforeStepAsync(OperationStages.Stopping, ct);
        await stages.StageReachedAsync(OperationStages.Stopping, null, ct);
        await stages.BeforeStepAsync(OperationStages.Removed, ct);
        Remove(modelId);
        await stages.StageReachedAsync(OperationStages.Removed, null, ct);
    }

    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) =>
        Task.FromResult(Instance(modelId) is null ? null : new LaunchConfig { Image = "local/vllm:test", Argv = ["--model", "/models/" + modelId] });

    public async IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        for (int i = 1; i <= tail; i++)
        {
            yield return "line " + i;
        }
    }

    public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => Task.FromResult<Uri?>(null);

    public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => Task.FromResult(false);
}
