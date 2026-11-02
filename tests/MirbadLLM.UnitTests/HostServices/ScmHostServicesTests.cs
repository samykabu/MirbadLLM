using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.HostServices.Windows;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Agent;

namespace MirbadLLM.UnitTests.HostServices;

#pragma warning disable xUnit1051 // hand-written fakes; TestContext.Current.CancellationToken forwarding is not needed everywhere.

/// <summary>
/// MN-228: <c>/host/services</c> over the Windows Service Control Manager (design §9.1, design-p3p4 §A.4.3). Every action
/// test runs against <see cref="FakeScm"/>: nothing here starts, stops or reconfigures a service of the machine the tests
/// run on. The one test that talks to the real SCM (<see cref="RealScmListsAndReadsServicesReadOnly"/>) only
/// enumerates and queries. The installer tests run the real <c>install-agent.ps1</c> functions in Windows PowerShell 5.1
/// on pure inputs (strings and a temporary file), never <c>sc sdset</c>.
/// </summary>
public sealed class ScmHostServicesTests : IDisposable
{
    private static readonly MasterTriple Master = new("master-a", 7, "boot-1");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-mn228-" + Guid.NewGuid().ToString("N"));

    public ScmHostServicesTests() => Directory.CreateDirectory(_dir);

    private string WhitelistPath => Path.Combine(_dir, ScmWhitelist.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- HostServiceManagers mapping (frozen by MN-202)

    [Theory]
    [InlineData(ScmServiceState.Running, "active", "running")]
    [InlineData(ScmServiceState.Stopped, "inactive", "stopped")]
    [InlineData(ScmServiceState.Paused, "inactive", "paused")]
    [InlineData(ScmServiceState.StartPending, "activating", "start_pending")]
    [InlineData(ScmServiceState.ContinuePending, "activating", "continue_pending")]
    [InlineData(ScmServiceState.StopPending, "deactivating", "stop_pending")]
    [InlineData(ScmServiceState.PausePending, "deactivating", "pause_pending")]
    public async Task ScmStatesMapOntoHostUnitExactlyAsHostServiceManagersDocuments(ScmServiceState state, string active, string sub)
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.State = state);
        node.Allow("ComfyUI");

        var unit = Assert.Single(await node.Client.ListAsync());

        Assert.Equal(active, unit.ActiveState);
        Assert.Equal(sub, unit.SubState);
    }

    [Theory]
    [InlineData(ScmStartType.Automatic, "enabled")]
    [InlineData(ScmStartType.AutomaticDelayed, "enabled")]
    [InlineData(ScmStartType.Manual, "disabled")]
    [InlineData(ScmStartType.Disabled, "masked")]
    public async Task StartTypesMapOntoUnitFileState(ScmStartType startType, string expected)
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.StartType = startType);
        node.Allow("ComfyUI");

        Assert.Equal(expected, Assert.Single(await node.Client.ListAsync()).UnitFileState);
    }

    [Fact]
    public async Task AWhitelistedServiceIsListedWithTheSystemScopeItsKeyNameAndItsProcess()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s =>
        {
            s.DisplayName = "ComfyUI server";
            s.Description = "Runs ComfyUI";
            s.ProcessId = 4242;
        });
        node.Allow("comfyui", displayName: "ComfyUI");

        var unit = Assert.Single(await node.Client.ListAsync());

        Assert.Equal("comfyui", unit.Name);
        Assert.Equal("ComfyUI", unit.Unit); // the SCM's spelling, whatever case the whitelist used
        Assert.Equal(HostServiceManagers.ScmScope, unit.Scope);
        Assert.Null(unit.User);
        Assert.Equal("ComfyUI", unit.DisplayName);
        Assert.Equal("Runs ComfyUI", unit.Description);
        Assert.Equal(4242, unit.MainPid);
        Assert.Equal(FakeScm.WorkingSet, unit.MemoryBytes);
        Assert.Equal(FakeScm.StartedAt.ToUnixTimeSeconds(), unit.ActiveSinceUnix);
        Assert.Null(unit.GpuMemoryBytes);
        Assert.Null(unit.Error);
    }

    [Fact]
    public async Task AServiceTheAgentHasNoRightsOnIsListedWithTheInstallerCommandAndItsActionsAreRefusedBeforeAnyCall()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.Rights = ScmRights.None);
        node.Allow("ComfyUI");

        var unit = Assert.Single(await node.Client.ListAsync());
        var start = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-1"));

        Assert.Contains("install-agent.ps1 -AllowService ComfyUI", unit.Error, StringComparison.Ordinal);
        AssertError(start, HttpStatusCode.Forbidden, ErrorCodes.Denied, "-AllowService ComfyUI");
        Assert.Empty(node.Scm.Calls);
    }

    // ---------------------------------------------------------------- acceptance: the deny list beats the whitelist

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("EventLog")]
    [InlineData("RpcSs")]
    [InlineData("Dnscache")]
    [InlineData("LanmanServer")]
    [InlineData("TermService")]
    [InlineData("wuauserv")]
    [InlineData("nvlddmkm")]
    [InlineData("NVDisplay.ContainerLocalSystem")]
    [InlineData("mirbadllm-agent")]
    public async Task TheDenyListIsEnforcedEvenForAWhitelistedName(string service)
    {
        var node = new Node(_dir);
        node.Scm.Add(service);
        // Written into the file by hand (the installer and the add route refuse it): the file is not trusted.
        string name = ScmWhitelist.DeriveName(service);
        File.WriteAllText(WhitelistPath, $$"""{"version":1,"services":[{"name":"{{name}}","service":"{{service.ToUpperInvariant()}}","protected":false}]}""");

        var start = await node.SubmitAsync(OperationKinds.ServiceStart, name, Action("op-start"));
        var stop = await node.SubmitAsync(OperationKinds.ServiceStop, name, Action("op-stop") with { Confirm = name });
        var restart = await node.SubmitAsync(OperationKinds.ServiceRestart, name, Action("op-restart") with { Confirm = name });
        var autostart = await node.SubmitAsync(OperationKinds.ServiceAutostart, name, Autostart("op-auto", false, name));
        var update = await node.SubmitAsync(OperationKinds.WhitelistUpdate, name, new HostWhitelistUpdateRequest
        {
            OperationId = "op-upd", IssuedAt = Now, ExpiresAt = Now.AddHours(1), DisplayName = "mine now",
        });
        var discovery = await node.Client.DiscoverAsync();

        foreach (var result in new[] { start, stop, restart, autostart, update })
        {
            AssertError(result, HttpStatusCode.Forbidden, ErrorCodes.Denied, "cannot be managed");
        }

        Assert.Empty(node.Scm.Calls);
        Assert.Empty(await node.Client.ListAsync());
        var row = Assert.Single(discovery.Discovered);
        Assert.True(row.Denied);
        Assert.False(row.Managed);
        Assert.NotNull(row.DeniedBy);
        Assert.Contains(service, discovery.DenyPatterns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADeniedServiceCannotBeWhitelistedAndTheFileStaysUntouched()
    {
        var node = new Node(_dir);
        node.Scm.Add("WinDefend");

        var add = await node.SubmitAsync(OperationKinds.WhitelistAdd, "windefend", Add("op-add", "windefend", confirm: "windefend"));

        AssertError(add, HttpStatusCode.Forbidden, ErrorCodes.Denied, "WinDefend");
        Assert.False(File.Exists(WhitelistPath));
        Assert.Throws<HostServiceDeniedException>(() => node.Whitelist.Add("windefend", null, false));
    }

    // ---------------------------------------------------------------- acceptance: the agent's own service is refused

    [Fact]
    public async Task TheAgentsOwnServiceIsRefusedByName()
    {
        var node = new Node(_dir, o => o.AgentServiceName = "my-agent");
        node.Scm.Add("my-agent");
        node.Scm.Add(WindowsServiceHosting.ServiceName);
        File.WriteAllText(WhitelistPath, """{"version":1,"services":[{"name":"my-agent","service":"my-agent"}]}""");

        var stop = await node.SubmitAsync(OperationKinds.ServiceStop, "my-agent", Action("op-stop"));
        var add = await node.SubmitAsync(OperationKinds.WhitelistAdd, WindowsServiceHosting.ServiceName,
            Add("op-add", WindowsServiceHosting.ServiceName, confirm: WindowsServiceHosting.ServiceName));

        AssertError(stop, HttpStatusCode.Forbidden, ErrorCodes.Denied, "agent");
        AssertError(add, HttpStatusCode.Forbidden, ErrorCodes.Denied, "cannot be managed");
        Assert.Empty(node.Scm.Calls);
    }

    [Fact]
    public async Task TheServiceTheAgentRunsInIsRefusedByItsProcessIdUnderAnyName()
    {
        var node = new Node(_dir);
        node.Scm.Add("RenamedAgent", s => s.ProcessId = Environment.ProcessId);
        File.WriteAllText(WhitelistPath, """{"version":1,"services":[{"name":"renamedagent","service":"RenamedAgent"}]}""");

        var restart = await node.SubmitAsync(OperationKinds.ServiceRestart, "renamedagent", Action("op-r"));
        var add = await node.SubmitAsync(OperationKinds.WhitelistAdd, "RenamedAgent", Add("op-add", "RenamedAgent", confirm: "renamedagent"));
        var discovery = await node.Client.DiscoverAsync();

        AssertError(restart, HttpStatusCode.Forbidden, ErrorCodes.Denied, "agent's own service");
        AssertError(add, HttpStatusCode.Forbidden, ErrorCodes.Denied, "agent's own service");
        Assert.Empty(node.Scm.Calls);
        Assert.Equal(ScmHostClient.OwnServiceMarker, Assert.Single(discovery.Discovered).DeniedBy);
    }

    // ---------------------------------------------------------------- acceptance: restarts after a crash end Unknown (D42)

    [Fact]
    public async Task ARestartDispatchedBeforeTheAgentDiedEndsUnknownWithTheObservedStateAndIsNotReplayed()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        node.Allow("ComfyUI");
        var request = Action("op-restart");
        await node.SeedAsync(OperationKinds.ServiceRestart, "comfyui", request, dispatched: true);

        await node.RecoverAsync();

        var status = node.Log.Find("op-restart")!;
        Assert.Equal(OperationState.Unknown, status.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, status.Error!.Code);
        Assert.Contains("Observed: active (running)", status.Error.Message, StringComparison.Ordinal);
        Assert.Equal("running", status.ReadResult<HostUnit>()!.SubState);
        Assert.Empty(node.Scm.Calls);

        // A same-id retry answers the stored Unknown again (D42), still without touching the service.
        var retry = await node.SubmitAsync(OperationKinds.ServiceRestart, "comfyui", request);
        AssertError(retry, HttpStatusCode.GatewayTimeout, ErrorCodes.OutcomeUnknown, "Outcome unknown");
        Assert.Empty(node.Scm.Calls);
    }

    [Fact]
    public async Task ARestartAcceptedButNotDispatchedFailsAfterACrash()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        node.Allow("ComfyUI");
        await node.SeedAsync(OperationKinds.ServiceRestart, "comfyui", Action("op-restart"), dispatched: false);

        await node.RecoverAsync();

        Assert.Equal(OperationState.Failed, node.Log.Find("op-restart")!.State);
        Assert.Empty(node.Scm.Calls);
    }

    [Fact]
    public async Task DesiredStateActionsAreReconciledAfterACrash()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.State = ScmServiceState.Running);
        node.Scm.Add("Ollama", s => s.State = ScmServiceState.Stopped);
        node.Allow("ComfyUI");
        node.Allow("Ollama");
        await node.SeedAsync(OperationKinds.ServiceStart, "comfyui", Action("op-done"), dispatched: true);
        await node.SeedAsync(OperationKinds.ServiceStart, "ollama", Action("op-again"), dispatched: true);

        await node.RecoverAsync();

        Assert.Equal(OperationState.Succeeded, node.Log.Find("op-done")!.State); // already running: nothing sent
        Assert.Equal(OperationState.Succeeded, node.Log.Find("op-again")!.State); // stopped: started again
        Assert.Equal(["start Ollama"], node.Scm.Calls);
        Assert.Equal(ScmServiceState.Running, node.Scm.Services["Ollama"].State);
    }

    [Fact]
    public async Task AStartStillPendingAfterTheActionTimeoutEndsUnknownWithTheObservedState()
    {
        var node = new Node(_dir, o => o.ActionTimeout = TimeSpan.FromMilliseconds(150));
        node.Scm.Add("ComfyUI", s =>
        {
            s.State = ScmServiceState.Stopped;
            s.StaysPending = true;
        });
        node.Allow("ComfyUI");

        var start = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-slow"));

        var error = AssertError(start, HttpStatusCode.GatewayTimeout, ErrorCodes.OutcomeUnknown, "start_pending");
        Assert.Contains("Observed: activating (start_pending)", error.Message, StringComparison.Ordinal);
        Assert.Equal(OperationState.Unknown, node.Log.Find("op-slow")!.State);
        Assert.Equal(["start ComfyUI"], node.Scm.Calls);
    }

    // ---------------------------------------------------------------- live actions

    [Fact]
    public async Task StartStopAndRestartDriveTheScmAndAnswerTheUnit()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.State = ScmServiceState.Stopped);
        node.Allow("ComfyUI");
        string? stageAtCall = null;
        node.Scm.OnCall = _ => stageAtCall ??= node.Log.Find("op-1")?.Stage;

        var start = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-1"));
        var startAgain = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-2"));
        var restart = await node.SubmitAsync(OperationKinds.ServiceRestart, "comfyui", Action("op-3"));
        var stop = await node.SubmitAsync(OperationKinds.ServiceStop, "comfyui", Action("op-4"));

        Assert.Equal(OperationStages.Dispatched, stageAtCall);
        Assert.Equal("running", Ok(start).ReadResult<HostUnit>()!.SubState);
        Assert.Equal("running", Ok(startAgain).ReadResult<HostUnit>()!.SubState); // already running is success (desired state)
        Assert.Equal("running", Ok(restart).ReadResult<HostUnit>()!.SubState);
        Assert.Equal("stopped", Ok(stop).ReadResult<HostUnit>()!.SubState);
        Assert.Equal(["start ComfyUI", "start ComfyUI", "stop ComfyUI", "start ComfyUI", "stop ComfyUI"], node.Scm.Calls);
    }

    [Fact]
    public async Task AProtectedServiceNeedsItsTypedNameToStopRestartOrLoseAutostart()
    {
        var node = new Node(_dir);
        node.Scm.Add("RustDesk", s => s.Rights = ScmRights.Start | ScmRights.Stop | ScmRights.ChangeConfig);
        node.Allow("RustDesk", isProtected: true);

        var stop = await node.SubmitAsync(OperationKinds.ServiceStop, "rustdesk", Action("op-1"));
        var restart = await node.SubmitAsync(OperationKinds.ServiceRestart, "rustdesk", Action("op-2") with { Confirm = "RustDesk" });
        var autostart = await node.SubmitAsync(OperationKinds.ServiceAutostart, "rustdesk", Autostart("op-3", false, confirm: null));
        var confirmed = await node.SubmitAsync(OperationKinds.ServiceStop, "rustdesk", Action("op-4") with { Confirm = "rustdesk" });

        AssertError(stop, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired, "protected");
        AssertError(restart, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired, "protected");
        AssertError(autostart, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired, "protected");
        Assert.Equal("stopped", Ok(confirmed).ReadResult<HostUnit>()!.SubState);
        Assert.Equal(["stop RustDesk"], node.Scm.Calls);
    }

    [Fact]
    public async Task AutostartChangesTheStartTypeOnlyWithTheChangeConfigRightAndOnlyWhenNeeded()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.StartType = ScmStartType.Manual);
        node.Scm.Add("Ollama", s =>
        {
            s.StartType = ScmStartType.Manual;
            s.Rights = ScmRights.Start | ScmRights.Stop | ScmRights.ChangeConfig;
        });
        node.Allow("ComfyUI");
        node.Allow("Ollama");

        var refused = await node.SubmitAsync(OperationKinds.ServiceAutostart, "comfyui", Autostart("op-1", true));
        var on = await node.SubmitAsync(OperationKinds.ServiceAutostart, "ollama", Autostart("op-2", true));
        var onAgain = await node.SubmitAsync(OperationKinds.ServiceAutostart, "ollama", Autostart("op-3", true));
        var off = await node.SubmitAsync(OperationKinds.ServiceAutostart, "ollama", Autostart("op-4", false));

        AssertError(refused, HttpStatusCode.Forbidden, ErrorCodes.Denied, "-AllowServiceAutostart");
        Assert.Equal("enabled", Ok(on).ReadResult<HostUnit>()!.UnitFileState);
        Assert.Equal("enabled", Ok(onAgain).ReadResult<HostUnit>()!.UnitFileState);
        Assert.Equal("disabled", Ok(off).ReadResult<HostUnit>()!.UnitFileState);
        Assert.Equal(["start-type Ollama Automatic", "start-type Ollama Manual"], node.Scm.Calls);
    }

    [Theory]
    [InlineData(ScmException.AccessDenied, HttpStatusCode.Forbidden, ErrorCodes.Denied)]
    [InlineData(ScmException.ServiceDisabled, HttpStatusCode.Conflict, ErrorCodes.Conflict)]
    [InlineData(ScmException.ServiceDoesNotExist, HttpStatusCode.NotFound, ErrorCodes.NotFound)]
    public async Task ScmRefusalsFailTheOperationWithTheirMeaning(int win32Error, HttpStatusCode status, string code)
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s =>
        {
            s.State = ScmServiceState.Stopped;
            s.FailStartWith = win32Error;
        });
        node.Allow("ComfyUI");

        var start = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-1"));

        AssertError(start, status, code, "ComfyUI");
        Assert.Equal(OperationState.Failed, node.Log.Find("op-1")!.State);
    }

    // ---------------------------------------------------------------- whitelist

    [Fact]
    public async Task WhitelistAddNeedsTheProposedNameAndRecordsTheScmsSpellingInTheStateFile()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI_Server", s => s.DisplayName = "ComfyUI server");

        var unconfirmed = await node.SubmitAsync(OperationKinds.WhitelistAdd, "comfyui_server", Add("op-1", "comfyui_server", confirm: null));
        var added = await node.SubmitAsync(OperationKinds.WhitelistAdd, "comfyui_server", Add("op-2", "comfyui_server", confirm: "comfyui-server"));
        var again = await node.SubmitAsync(OperationKinds.WhitelistAdd, "ComfyUI_Server", Add("op-3", "ComfyUI_Server", confirm: "comfyui-server"));
        var missing = await node.SubmitAsync(OperationKinds.WhitelistAdd, "NoSuch", Add("op-4", "NoSuch", confirm: "nosuch"));

        AssertError(unconfirmed, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired, "comfyui-server");
        var unit = Ok(added).ReadResult<HostUnit>()!;
        Assert.Equal(("comfyui-server", "ComfyUI_Server", "ComfyUI server"), (unit.Name, unit.Unit, unit.DisplayName));
        AssertError(again, HttpStatusCode.Conflict, ErrorCodes.Conflict, "already managed");
        AssertError(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound, "NoSuch");
        Assert.Equal("comfyui-server", Assert.Single(new ScmWhitelist(WhitelistPath, ScmDenyList.BuiltIn, NullLogger.Instance).Read()).Name);
        Assert.Empty(node.Scm.Calls);
    }

    [Fact]
    public async Task UpdateAndRemoveChangeOnlyTheWhitelist()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        node.Allow("ComfyUI", isProtected: true);

        var unprotect = await node.SubmitAsync(OperationKinds.WhitelistUpdate, "comfyui", new HostWhitelistUpdateRequest
        {
            OperationId = "op-1", IssuedAt = Now, ExpiresAt = Now.AddHours(1), Protected = false,
        });
        var rename = await node.SubmitAsync(OperationKinds.WhitelistUpdate, "comfyui", new HostWhitelistUpdateRequest
        {
            OperationId = "op-2", IssuedAt = Now, ExpiresAt = Now.AddHours(1), DisplayName = "Comfy", Protected = false, Confirm = "comfyui",
        });
        var remove = await node.SubmitAsync(OperationKinds.WhitelistRemove, "comfyui", new HostWhitelistRemoveRequest
        {
            OperationId = "op-3", IssuedAt = Now, ExpiresAt = Now.AddHours(1),
        });

        AssertError(unprotect, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired, "unprotect");
        var unit = Ok(rename).ReadResult<HostUnit>()!;
        Assert.Equal(("Comfy", false), (unit.DisplayName, unit.Protected));
        Ok(remove);
        Assert.Empty(await node.Client.ListAsync());
        Assert.Empty(node.Scm.Calls);
        Assert.Equal(ScmServiceState.Running, node.Scm.Services["ComfyUI"].State);
    }

    [Fact]
    public async Task TheWhitelistFileIsReadOnEveryCallSoTheInstallersEntriesCountAtOnce()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        Assert.Empty(await node.Client.ListAsync());

        File.WriteAllText(WhitelistPath, """{"version":1,"services":[{"name":"comfyui","service":"ComfyUI","displayName":"ComfyUI","protected":false}]}""");

        Assert.Equal("comfyui", Assert.Single(await node.Client.ListAsync()).Name);
    }

    [Fact]
    public async Task AMalformedWhitelistFailsClosed()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        File.WriteAllText(WhitelistPath, """{"version":1,"services":[{"name":"Bad Name","service":"ComfyUI"}]}""");
        var queries = node.Provider.GetRequiredService<HostServiceQueries>();

        var list = await queries.ListAsync(null);
        var start = await node.SubmitAsync(OperationKinds.ServiceStart, "comfyui", Action("op-1"));

        Assert.False(list.HostdAvailable);
        Assert.Contains("invalid name", list.HostdError, StringComparison.Ordinal);
        Assert.Equal(OperationState.Failed, node.Log.Find("op-1")!.State);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Empty(node.Scm.Calls);
    }

    [Theory]
    [InlineData("ComfyUI", "comfyui")]
    [InlineData("ComfyUI_Server", "comfyui-server")]
    [InlineData("MSSQL$SQLEXPRESS", "mssql-sqlexpress")]
    [InlineData("NVDisplay.ContainerLocalSystem", "nvdisplay-containerlocalsystem")]
    [InlineData("--__--", "service")]
    [InlineData("Ünïcode Svc", "n-code-svc")]
    public void DerivedNamesAreUrlSafe(string service, string expected) => Assert.Equal(expected, ScmWhitelist.DeriveName(service));

    [Fact]
    public async Task DiscoveryListsEveryServiceFlaggedManagedOrDenied()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI");
        node.Scm.Add("Spooler", s =>
        {
            s.State = ScmServiceState.Stopped;
            s.OwnProcess = false;
        });
        node.Scm.Add("EventLog");
        node.Allow("ComfyUI");

        var discovery = await node.Client.DiscoverAsync();

        Assert.Equal(["ComfyUI", "EventLog", "Spooler"], discovery.Discovered.Select(d => d.Unit));
        var comfy = discovery.Discovered[0];
        Assert.Equal((true, false, "comfyui", "active"), (comfy.Managed, comfy.Denied, comfy.Name, comfy.ActiveState));
        Assert.Equal(("EventLog", true), (discovery.Discovered[1].DeniedBy, discovery.Discovered[1].Denied));
        var spooler = discovery.Discovered[2];
        Assert.Equal(("spooler", false, "inactive", "stopped"), (spooler.Name, spooler.Managed, spooler.ActiveState, spooler.SubState));
        Assert.Null(spooler.MemoryBytes);
        Assert.All(discovery.Discovered, d => Assert.Equal(HostServiceManagers.ScmScope, d.Scope));
        Assert.Equal(ScmDenyList.BuiltInPatterns, discovery.DenyPatterns);
        Assert.Equal("comfyui", Assert.Single(discovery.Services).Name);
    }

    // ---------------------------------------------------------------- capability and composition

    [Fact]
    public async Task TheContributorReportsServicesScmWithTheScmManagerAndNothingOfLinux()
    {
        var node = new Node(_dir);
        var report = await ReportAsync(node.Provider);

        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.ServicesScm], report.Features);
        Assert.Equal(new HostServicesCapability(HostServiceManagers.Scm, true, null, false), report.HostServices);
        Assert.Null(report.BackupMount);
        Assert.True(AgentRoutes.HostServiceStart.IsOfferedBy(report.Features));
        Assert.True(AgentRoutes.HostWhitelistAdd.IsOfferedBy(report.Features));
        Assert.False(AgentRoutes.HostContainerStart.IsOfferedBy(report.Features));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddScmHostServicesReplacesTheHostdPackageInEitherOrder(bool scmFirst)
    {
        var scm = new FakeScm();
        scm.Add("ComfyUI");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationLog>(new FakeOperationLog(TimeProvider.System));
        services.AddSingleton<IFencingState>(new FakeFencing { AcceptedTriple = Master });
        var options = ScmHostServicesOptions.ForStateDirectory(_dir);

        // The Windows configuration of the Linux package, as agent.windows.example.json sets it.
        void Linux() => services.AddAgentHostServices(o =>
        {
            o.Systemd = false;
            o.Containers = false;
            o.BackupMount = false;
        });

        if (scmFirst)
        {
            services.AddScmHostServices(options, _ => scm);
            Linux();
        }
        else
        {
            Linux();
            services.AddScmHostServices(options, _ => scm);
        }

        using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<HostServiceOperations>();
        var handler = provider.GetServices<IOperationRecoveryHandler>().First(h => h.Kinds.Contains(OperationKinds.ServiceRestart));
        var report = await ReportAsync(provider);

        Assert.IsType<ScmHostClient>(provider.GetRequiredService<IHostdClient>());
        Assert.Null(operations.Unsupported(OperationKinds.ServiceStart));
        Assert.NotNull(operations.Unsupported(OperationKinds.ContainerStart));
        Assert.NotNull(operations.Unsupported(OperationKinds.BackupMountSet));
        Assert.Same(provider.GetRequiredService<HostServiceRecoveryHandler>(), handler);
        Assert.Contains(AgentFeatures.ServicesScm, report.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesSystemd, report.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesDocker, report.Features);
        Assert.DoesNotContain(AgentFeatures.BackupMount, report.Features);
        Assert.Equal(HostServiceManagers.Scm, report.HostServices!.Manager);
    }

    [Fact]
    public async Task TheHostServiceRoutesServeTheScmOverHttp()
    {
        var scm = new FakeScm();
        scm.Add("ComfyUI", s => s.State = ScmServiceState.Stopped);
        File.WriteAllText(WhitelistPath, """{"version":1,"services":[{"name":"comfyui","service":"ComfyUI"}]}""");
        var log = new FakeOperationLog(TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOperationLog>(log);
        builder.Services.AddSingleton<IFencingState>(new FakeFencing { AcceptedTriple = Master });
        builder.Services.AddScmHostServices(ScmHostServicesOptions.ForStateDirectory(_dir), _ => scm);
        await using var app = builder.Build();
        app.MapGroup(Protocol.BasePath).MapHostServiceEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        var list = await client.GetFromJsonAsync<HostServicesResponse>(Protocol.BasePath + "/host/services?include=all", AgentJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, Protocol.BasePath + "/host/services/comfyui/start")
        {
            Content = JsonContent.Create(Action("op-http"), options: AgentJson.Options),
        };
        request.Headers.Add(Protocol.MasterHeader, Master.ToHeaderValue());
        using var response = await client.SendAsync(request);
        using var backup = await client.GetAsync(Protocol.BasePath + "/host/backup-mount");

        Assert.True(list!.HostdAvailable);
        Assert.Equal("stopped", Assert.Single(list.Units).SubState);
        Assert.Equal("ComfyUI", Assert.Single(list.Discovered!).Unit);
        Assert.False(list.ContainersAvailable);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<OperationStatus>(AgentJson.Options))!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal("running", status.ReadResult<HostUnit>()!.SubState);
        Assert.Equal(HttpStatusCode.NotImplemented, backup.StatusCode);
        Assert.Equal(["start ComfyUI"], scm.Calls);
    }

    // ---------------------------------------------------------------- the SCM's own wording (MN-239 item 1, D130)

    [Fact]
    public async Task TheScmNodesTextsNameTheServiceControlManagerAndNeverHostdASocketOrAMaskedUnit()
    {
        var node = new Node(_dir);
        node.Scm.Add("ComfyUI", s => s.StartType = ScmStartType.Disabled);
        node.Allow("ComfyUI");
        var queries = node.Provider.GetRequiredService<HostServiceQueries>();

        // GPU processes come from the Windows GPU probe, not through this client; an observed unit's start type is a
        // Windows start type; and with no SCM at all there is no socket to miss.
        var gpu = await queries.GetGpuProcessesAsync();
        var observed = await node.Provider.GetRequiredService<HostServiceOperations>()
            .ObserveAsync(new HostMutation(OperationKinds.ServiceStart, "comfyui", Action("op-observe")));
        var backup = await Assert.ThrowsAsync<HostServiceException>(() => node.Client.GetBackupMountAsync());
        node.Scm.IsAvailable = false;
        var noScm = await queries.GetGpuProcessesAsync();

        // The codes are the ones A.12 and operations.md pin; only the words are the SCM's.
        var gpuError = AssertError(gpu, HttpStatusCode.NotImplemented, ErrorCodes.NotSupported, "Service Control Manager");
        var noScmError = AssertError(noScm, HttpStatusCode.ServiceUnavailable, ErrorCodes.HostdUnavailable, "could not be opened");
        Assert.Equal("active (running), start type disabled", observed.Description);
        foreach (string text in new[] { gpuError.Message, noScmError.Message, observed.Description, backup.Message })
        {
            Assert.DoesNotContain("hostd", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("socket", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("masked", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnAmbiguousScmCallSaysSoInWindowsWordsAndKeepsTheWireCode()
    {
        var timeout = new TaskCanceledException("the SCM call was still waiting");

        var onScm = HostServiceErrors.FromHostd(timeout, HostServiceManagers.Scm);
        var onHostd = HostServiceErrors.FromHostd(timeout);

        Assert.Equal(ErrorCodes.Timeout, onScm.Error.Code);
        Assert.True(onScm.Ambiguous);
        Assert.StartsWith("The Windows Service Control Manager did not answer in time.", onScm.Error.Message, StringComparison.Ordinal);
        Assert.Contains(HostServiceErrors.OutcomeUnknownHint, onScm.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hostd", onScm.Error.Message, StringComparison.OrdinalIgnoreCase);

        // The Linux leg is untouched, word for word.
        Assert.Equal("mirbadllm-hostd did not answer in time. " + HostServiceErrors.OutcomeUnknownHint, onHostd.Error.Message);
        Assert.Equal("autostart masked", HostServiceWording.Autostart(HostServiceManagers.Systemd, "masked"));
        Assert.Equal("The host agent is not installed (no socket).", HostServiceWording.NotInstalled(HostServiceManagers.Systemd));

        // The Windows words of the frozen start-type mapping (enabled → automatic, disabled → manual, masked → disabled).
        Assert.Equal(
            ["start type automatic", "start type manual", "start type disabled", "start type unknown"],
            StartTypeWords.Select(s => HostServiceWording.Autostart(HostServiceManagers.Scm, s)));
    }

    /// <summary>The frozen systemd start-type vocabulary, in the order the Windows mapping is asserted in.</summary>
    private static readonly string[] StartTypeWords = ["enabled", "disabled", "masked", ""];

    // ---------------------------------------------------------------- the real SCM (read-only)

    [Fact]
    [Trait("Os", "Windows")]
    public async Task RealScmListsAndReadsServicesReadOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Service Control Manager exists only on Windows.");
            return;
        }

        var scm = new WindowsServiceControlManager();
        var client = new ScmHostClient(scm, new ScmWhitelist(WhitelistPath, ScmDenyList.BuiltIn, NullLogger.Instance), ScmDenyList.BuiltIn,
            ScmHostServicesOptions.ForStateDirectory(_dir), TimeProvider.System, NullLogger<ScmHostClient>.Instance);

        // Enumeration and queries only: nothing is started, stopped or reconfigured.
        Assert.True(scm.IsAvailable);
        var all = scm.List();
        var eventLog = scm.Find("eventlog");
        var discovery = await client.DiscoverAsync();
        var report = await new ScmHostServicesContributor(scm).ContributeAsync(BaseReport());

        Assert.True(all.Count > 20, $"only {all.Count} services listed");
        Assert.NotNull(eventLog);
        Assert.Equal("EventLog", eventLog.Name); // the SCM's spelling for a lower-case query
        Assert.Equal(ScmServiceState.Running, eventLog.State);
        Assert.NotNull(eventLog.ProcessId);
        Assert.Equal(ScmStartType.Automatic, eventLog.StartType);
        Assert.NotEmpty(eventLog.Description);
        Assert.Null(scm.Find("no-such-service-" + Guid.NewGuid().ToString("N")));
        var row = Assert.Single(discovery.Discovered, d => d.Unit == "EventLog");
        Assert.Equal(("active", "running", "enabled", true, "EventLog"), (row.ActiveState, row.SubState, row.UnitFileState, row.Denied, row.DeniedBy));
        Assert.Empty(discovery.DiscoveryErrors);
        Assert.Empty(discovery.Services);
        Assert.All(discovery.Discovered, d => Assert.Matches("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", d.Name));
        Assert.True(report.HostServices!.HostdAvailable);
    }

    // ---------------------------------------------------------------- install-agent.ps1 -AllowService

    [Fact]
    public void TheInstallersDenyListIsTheAgentsCompiledOne()
    {
        string function = FunctionText(InstallerText(), "Get-ScmDenyList");
        string list = Regex.Match(function, @"@\((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
        var names = Regex.Matches(list, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(ScmDenyList.BuiltInPatterns, names);
    }

    [Fact]
    public void TheInstallerGrantsStartStopAndQueryOnlyUnlessAutostartIsAsked()
    {
        string function = FunctionText(InstallerText(), "Get-ScmAgentRights");
        var rights = Regex.Matches(function, "return '([A-Z]+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(2, rights.Count);
        string withAutostart = rights[0];
        string plain = rights[1];
        Assert.Equal(["CC", "LC", "LO", "RC", "RP", "WP"], Pairs(plain));
        Assert.Equal(["CC", "DC", "LC", "LO", "RC", "RP", "WP"], Pairs(withAutostart));
        Assert.Contains($"'{ScmWhitelist.FileName}'", FunctionText(InstallerText(), "Invoke-AllowService"), StringComparison.Ordinal);

        static List<string> Pairs(string r) => [.. r.Chunk(2).Select(c => new string(c)).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData(
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)",
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCLORPWPRC;;;{SID})S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)")]
    [InlineData(
        "O:SYG:SYD:PAI(A;;CCLCSWRPWPDTLOCRRC;;;SY)",
        "O:SYG:SYD:PAI(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCLORPWPRC;;;{SID})")]
    [InlineData(
        "D:(A;;LC;;;{SID})(D;;WP;;;IU)(A;;CC;;;AU)",
        "D:(D;;WP;;;IU)(A;;CC;;;AU)(A;;CCLCLORPWPRC;;;{SID})")]
    [InlineData(
        "D:(XA;;RP;;;WD;(@User.x == 1))(A;;CC;;;SY)",
        "D:(XA;;RP;;;WD;(@User.x == 1))(A;;CC;;;SY)(A;;CCLCLORPWPRC;;;{SID})")]
    public void AddServiceSddlAceAppendsOneAllowAceToTheDaclAndKeepsTheRest(string before, string after)
    {
        string sid = ServiceProfileKeySweep.ServiceSidFor(WindowsServiceHosting.ServiceName);

        (int exit, string output) = RunWindowsPowerShell(LoadInstallerFunctions() +
            $"Add-ServiceSddlAce {Quote(before.Replace("{SID}", sid, StringComparison.Ordinal))} {Quote(sid)} 'CCLCLORPWPRC'");

        Assert.True(exit == 0, output);
        Assert.Equal(after.Replace("{SID}", sid, StringComparison.Ordinal), output.Trim());
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void TheInstallersServiceSidAndNamesMatchTheAgents()
    {
        string[] services = ["ComfyUI", "ComfyUI_Server", "MSSQL$SQLEXPRESS", "NVDisplay.ContainerLocalSystem", "--__--", "Ünïcode Svc"];
        string[] probes = ["windefend", "EVENTLOG", "MirbadLLM-Agent", "ComfyUI"];
        string names = string.Join("; ", services.Select(s => $"Get-ScmWhitelistName {Quote(s)}"));
        string denied = string.Join("; ", probes.Select(s => $"[string](Get-ScmDeniedBy {Quote(s)})"));

        // sc.exe showsid only derives the SID from the name (read-only).
        (int exit, string output) = RunWindowsPowerShell(LoadInstallerFunctions() +
            $"@{{ sid = (Get-ServiceSid 'mirbadllm-agent'); names = @({names}); denied = @({denied}) }} | ConvertTo-Json -Compress");

        Assert.True(exit == 0, output);
        var result = JsonNode.Parse(output.Trim())!;
        Assert.Equal(ServiceProfileKeySweep.ServiceSidFor(WindowsServiceHosting.ServiceName), result["sid"]!.GetValue<string>());
        Assert.Equal(services.Select(ScmWhitelist.DeriveName), result["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["WinDefend", "EventLog", "mirbadllm-agent", ""], result["denied"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["WinDefend", "EventLog", WindowsServiceHosting.ServiceName], probes.Take(3).Select(s => ScmDenyList.BuiltIn.Match(s)));
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void TheInstallersWhitelistEntryIsReadByTheAgentAndARerunAddsNothing()
    {
        string path = WhitelistPath;
        File.WriteAllText(path, """{"version":1,"services":[{"name":"ollama","service":"Ollama","displayName":"Ollama","protected":true}]}""");

        (int exit, string output) = RunWindowsPowerShell(LoadInstallerFunctions() +
            $"Add-ScmWhitelistEntry {Quote(path)} 'ComfyUI_Server' 'ComfyUI server'; Add-ScmWhitelistEntry {Quote(path)} 'comfyui_server' 'again'");

        Assert.True(exit == 0, output);
        Assert.Contains("already on the agent's service whitelist", output, StringComparison.Ordinal);
        var entries = new ScmWhitelist(path, ScmDenyList.BuiltIn, NullLogger.Instance).Read();
        Assert.Equal(
            [new ScmWhitelistEntry("ollama", "Ollama", "Ollama", true, null), new ScmWhitelistEntry("comfyui-server", "ComfyUI_Server", "ComfyUI server", false, null)],
            entries);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void TheInstallersFirstWhitelistEntryIsAJsonArray()
    {
        // Windows PowerShell 5.1 unrolls one-element arrays easily: the agent must still read a list.
        (int exit, string output) = RunWindowsPowerShell(LoadInstallerFunctions() + $"Add-ScmWhitelistEntry {Quote(WhitelistPath)} 'ComfyUI' ''");

        Assert.True(exit == 0, output);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(File.ReadAllText(WhitelistPath)).RootElement.GetProperty("services").ValueKind);
        Assert.Equal(new ScmWhitelistEntry("comfyui", "ComfyUI", "ComfyUI", false, null),
            Assert.Single(new ScmWhitelist(WhitelistPath, ScmDenyList.BuiltIn, NullLogger.Instance).Read()));
    }

    // ---------------------------------------------------------------- helpers

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static HostServiceActionRequest Action(string id) => new() { OperationId = id, IssuedAt = Now, ExpiresAt = Now.AddHours(1) };

    private static HostServiceAutostartRequest Autostart(string id, bool enabled, string? confirm = null) =>
        new() { OperationId = id, IssuedAt = Now, ExpiresAt = Now.AddHours(1), Enabled = enabled, Confirm = confirm };

    private static HostWhitelistAddRequest Add(string id, string unit, string? confirm) =>
        new() { OperationId = id, IssuedAt = Now, ExpiresAt = Now.AddHours(1), Unit = unit, Scope = HostServiceManagers.ScmScope, Confirm = confirm };

    private static OperationStatus Ok(HostCallResult result)
    {
        Assert.True(result.StatusCode == 200, JsonSerializer.Serialize(result.Body, AgentJson.Options));
        var status = Assert.IsType<OperationStatus>(result.Body);
        Assert.Equal(OperationState.Succeeded, status.State);
        return status;
    }

    private static AgentError AssertError(HostCallResult result, HttpStatusCode status, string code, string messagePart)
    {
        var body = Assert.IsType<AgentErrorResponse>(result.Body);
        Assert.True((int)status == result.StatusCode, $"{result.StatusCode}: {body.Error.Code} {body.Error.Message}");
        Assert.Equal(code, body.Error.Code);
        Assert.Contains(messagePart, body.Error.Message, StringComparison.Ordinal);
        return body.Error;
    }

    private static NodeCapabilities BaseReport() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Features = [AgentFeatures.TelemetryV1],
        Os = new NodeOsInfo("windows", "Windows 11"),
        Arch = "x64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
    };

    private static async Task<NodeCapabilities> ReportAsync(IServiceProvider provider)
    {
        var report = BaseReport();
        foreach (var contributor in provider.GetServices<ICapabilityContributor>())
        {
            report = await contributor.ContributeAsync(report);
        }

        return report;
    }

    /// <summary>The SCM host services as the composition registers them, over a fake SCM and an in-memory operation log.</summary>
    private sealed class Node
    {
        public Node(string stateDirectory, Action<ScmHostServicesOptions>? configure = null)
        {
            var options = ScmHostServicesOptions.ForStateDirectory(stateDirectory);
            options.PollInterval = TimeSpan.FromMilliseconds(5);
            configure?.Invoke(options);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOperationLog>(Log);
            services.AddSingleton<IFencingState>(Fencing);
            services.AddScmHostServices(options, _ => Scm);
            Provider = services.BuildServiceProvider();
            Client = Provider.GetRequiredService<ScmHostClient>();
            Whitelist = Provider.GetRequiredService<ScmWhitelist>();
        }

        public FakeScm Scm { get; } = new();

        public FakeOperationLog Log { get; } = new(TimeProvider.System);

        public FakeFencing Fencing { get; } = new() { AcceptedTriple = Master };

        public ServiceProvider Provider { get; }

        public ScmHostClient Client { get; }

        public ScmWhitelist Whitelist { get; }

        /// <summary>Whitelists a service directly in the file (as the installer does).</summary>
        public void Allow(string service, string? displayName = null, bool isProtected = false) =>
            Whitelist.Add(service, displayName, isProtected);

        public Task<HostCallResult> SubmitAsync(string kind, string target, AgentMutation request) =>
            Provider.GetRequiredService<HostServiceOperations>().SubmitAsync(new HostMutation(kind, target, request), Master.ToHeaderValue(),
                JsonSerializer.SerializeToElement(request, request.GetType(), AgentJson.Options));

        /// <summary>Records an operation as accepted (and dispatched) without running it: the agent died right there.</summary>
        public async Task SeedAsync(string kind, string target, AgentMutation request, bool dispatched)
        {
            await Log.AcceptAsync(new OperationSubmission
            {
                OperationId = request.OperationId, Kind = kind, Target = target, IssuedAt = request.IssuedAt, ExpiresAt = request.ExpiresAt,
                Master = Master, Payload = HostMutation.Redact(JsonSerializer.SerializeToElement(request, request.GetType(), AgentJson.Options)),
            });
            if (dispatched)
            {
                await Log.RecordStageAsync(request.OperationId, new OperationStageUpdate { Stage = OperationStages.Dispatched, State = OperationState.Running });
            }
        }

        /// <summary>The startup reconciliation's step for host-service kinds: the registered handler for every open operation.</summary>
        public async Task RecoverAsync()
        {
            var handlers = Provider.GetServices<IOperationRecoveryHandler>().ToList();
            foreach (var operation in Log.ListRecoverable())
            {
                var handler = handlers.FirstOrDefault(h => h.Kinds.Contains(operation.Status.Kind));
                Assert.NotNull(handler);
                await handler.RecoverAsync(operation);
            }

            Assert.Empty(Log.ListActive());
        }
    }

    // ---------------------------------------------------------------- Windows PowerShell 5.1

    private static string InstallerText() => File.ReadAllText(InstallerPath());

    private static string InstallerPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
            {
                return Path.Combine(dir.FullName, "deploy", "agent", "windows", "install-agent.ps1");
            }
        }

        throw new DirectoryNotFoundException("MirbadLLM.slnx not found above the test binaries.");
    }

    private static string FunctionText(string script, string name)
    {
        Match m = Regex.Match(script.Replace("\r", "", StringComparison.Ordinal), $@"function {Regex.Escape(name)}\b.*?\n\}}", RegexOptions.Singleline);
        Assert.True(m.Success, $"function {name} not found");
        return m.Value;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Defines every top-level function of install-agent.ps1 without running its main part.</summary>
    private static string LoadInstallerFunctions() =>
        "$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest; $e = $null; " +
        $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quote(InstallerPath())}, [ref]$null, [ref]$e); " +
        "foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([scriptblock]::Create($fn.Extent.Text)) }; ";

    private static (int Exit, string Output) RunWindowsPowerShell(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows PowerShell 5.1 exists only on Windows.");
        }

        string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe))
        {
            Assert.Skip($"No Windows PowerShell at {exe}.");
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            ArgumentList =
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; " + command)),
            },
        };
        start.Environment.Remove("PSModulePath");
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("powershell.exe did not finish within 120 s.");
        }

        return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stdout.Result + stderr.Result);
    }
}

/// <summary>
/// A scripted Service Control Manager: services by key name (case-insensitive, as Windows), the rights the agent holds
/// on each, and every mutating call recorded. It never touches the real SCM.
/// </summary>
internal sealed class FakeScm : IServiceControlManager
{
    public const long WorkingSet = 734_003_200;

    public static readonly DateTimeOffset StartedAt = new(2026, 10, 8, 9, 30, 0, TimeSpan.Zero);

    public sealed class Svc
    {
        public required string Name { get; init; }

        public string DisplayName { get; set; } = "";

        public ScmServiceState State { get; set; } = ScmServiceState.Running;

        public int? ProcessId { get; set; } = 1234;

        public bool OwnProcess { get; set; } = true;

        public ScmStartType? StartType { get; set; } = ScmStartType.Automatic;

        public string Description { get; set; } = "";

        public ScmRights Rights { get; set; } = ScmRights.Start | ScmRights.Stop;

        /// <summary>A start leaves it StartPending forever.</summary>
        public bool StaysPending { get; set; }

        /// <summary>The Win32 error StartService fails with.</summary>
        public int? FailStartWith { get; set; }
    }

    public Dictionary<string, Svc> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    /// <summary>Called before every mutating call.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>False for a node where the SCM cannot be opened at all (MN-239 item 1).</summary>
    public bool IsAvailable { get; set; } = true;

    public Svc Add(string name, Action<Svc>? configure = null)
    {
        var svc = new Svc { Name = name, DisplayName = name };
        configure?.Invoke(svc);
        Services[name] = svc;
        return svc;
    }

    public IReadOnlyList<ScmService> List() => [.. Services.Values.Select(ToRecord)];

    public ScmService? Find(string serviceName) => Services.TryGetValue(serviceName, out var s) ? ToRecord(s) : null;

    public ScmRights RightsOn(string serviceName) => Get(serviceName).Rights;

    public void StartService(string serviceName)
    {
        var s = Get(serviceName);
        Record($"start {s.Name}");
        if (s.FailStartWith is { } error)
        {
            throw new ScmException(error, $"start failed ({error})");
        }

        if (s.State == ScmServiceState.Running)
        {
            throw new ScmException(ScmException.ServiceAlreadyRunning, "already running");
        }

        s.State = s.StaysPending ? ScmServiceState.StartPending : ScmServiceState.Running;
        s.ProcessId = 5678;
    }

    public void StopService(string serviceName)
    {
        var s = Get(serviceName);
        Record($"stop {s.Name}");
        if (s.State == ScmServiceState.Stopped)
        {
            throw new ScmException(ScmException.ServiceNotActive, "not active");
        }

        s.State = ScmServiceState.Stopped;
        s.ProcessId = null;
    }

    public void SetStartType(string serviceName, ScmStartType startType)
    {
        var s = Get(serviceName);
        Record($"start-type {s.Name} {startType}");
        s.StartType = startType;
    }

    public long? WorkingSetBytes(int processId) => WorkingSet;

    public DateTimeOffset? ProcessStartedAt(int processId) => StartedAt;

    private void Record(string call)
    {
        OnCall?.Invoke(call);
        Calls.Add(call);
    }

    private Svc Get(string name) =>
        Services.TryGetValue(name, out var s) ? s : throw new ScmException(ScmException.ServiceDoesNotExist, $"no service {name}");

    private static ScmService ToRecord(Svc s) =>
        new(s.Name, s.DisplayName, s.State, s.State == ScmServiceState.Stopped ? null : s.ProcessId, s.OwnProcess, s.StartType, s.Description);
}
