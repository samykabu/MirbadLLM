using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Host services (design §4.3 "Host services", §4.4 item 8, §10.3): a simulated hostd (whitelisted units, discovery, deny
/// list, typed-name confirmations as hostd checks them) and Docker (non-model containers, the agent's deny rules), the
/// backup mount (password never stored) and GPU processes. Every mutation records <c>dispatched</c> right before the
/// simulated call; after a crash desired-state actions are re-applied and unverifiable ones end <c>Unknown</c>.
/// </summary>
internal sealed partial class FakeAgent
{
    private static readonly string[] DenyPatterns = ["systemd-*", "dbus*", "ssh*", "docker*", "containerd*"];
    private static readonly string[] RestartPolicies = ["no", "always", "unless-stopped", "on-failure"];

    private readonly Dictionary<string, FakeUnit> _units = new(StringComparer.Ordinal);
    private readonly List<FakeUnit> _discovered = [];
    private readonly Dictionary<string, HostContainerInfo> _containers = new(StringComparer.Ordinal);
    private BackupMountInfo _backup = UnconfiguredBackup();
    private bool _hostdUp = true;
    private bool _dockerUp = true;
    private bool _hostdTimesOut;
    private int _nextPid = 4100;

    /// <summary>hostd answers or not (503 <c>hostd_unavailable</c>).</summary>
    public void SetHostdAvailable(bool available)
    {
        lock (_gate)
        {
            bool docker = _dockerUp, hostd = _hostdUp;
            _hostdUp = available;
            PublishHealthIfChangedLocked(docker, hostd);
        }
    }

    /// <summary>Docker answers or not (503 <c>docker_unavailable</c>).</summary>
    public void SetDockerAvailable(bool available)
    {
        lock (_gate)
        {
            bool docker = _dockerUp, hostd = _hostdUp;
            _dockerUp = available;
            PublishHealthIfChangedLocked(docker, hostd);
        }
    }

    /// <summary>hostd accepts calls but never answers: host-service operations end <c>Unknown</c> with 504 <c>outcome_unknown</c> (§4.4 item 8).</summary>
    public void SetHostdTimingOut(bool timingOut)
    {
        lock (_gate)
        {
            _hostdTimesOut = timingOut;
        }
    }

    private static BackupMountInfo UnconfiguredBackup() => new() { Target = "/mnt/mirbadllm-backup", Unit = "mnt-mirbadllm\\x2dbackup.mount" };

    private void SeedHostLocked()
    {
        _hostdUp = _options.HostdAvailable;
        AddUnitLocked(new FakeUnit(AgentContractSeed.Service, "contract-svc.service", "Contract service") { Managed = true });
        AddUnitLocked(new FakeUnit(AgentContractSeed.ProtectedService, "contract-protected.service", "Contract protected service") { Managed = true, Protected = true });
        _discovered.Add(new FakeUnit("contract-extra", "contract-extra.service", "Contract extra service"));
        _discovered.Add(new FakeUnit("systemd-journald", "systemd-journald.service", "Journal service"));
        AddContainerLocked(AgentContractSeed.Container, "local/contract-tool:1", new Dictionary<string, string>());
        AddContainerLocked("mirbadllm", "local/mirbadllm:latest", new Dictionary<string, string>());
        AddContainerLocked("mirbadllm-agent", "local/mirbadllm-agent:latest", new Dictionary<string, string>());
    }

    private void AddUnitLocked(FakeUnit unit)
    {
        unit.ActiveState = "active";
        unit.SubState = "running";
        unit.UnitFileState = "enabled";
        unit.MainPid = _nextPid++;
        unit.ActiveSince = Now.ToUnixTimeSeconds();
        _units[unit.Name] = unit;
    }

    private void AddContainerLocked(string name, string image, Dictionary<string, string> labels) =>
        _containers[name] = new HostContainerInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            State = "running",
            Status = "Up",
            Image = image,
            Labels = labels,
            RestartPolicy = "unless-stopped",
            StartedAt = Now,
        };

    private static string? DeniedBy(string unit) =>
        DenyPatterns.FirstOrDefault(p => p.EndsWith('*') ? unit.StartsWith(p[..^1], StringComparison.Ordinal) : unit == p);

    /// <summary>The agent's container deny rules (design §10.3): its own and the master's containers, model and download containers.</summary>
    private bool IsDeniedContainer(string name) =>
        name is "mirbadllm" or "mirbadllm-agent" || name.StartsWith("ss-", StringComparison.Ordinal) ||
        (_containers.TryGetValue(name, out var info) && (info.Labels.ContainsKey(ContainerLabels.Download) || info.Labels.ContainsKey(ContainerLabels.Model)));

    private void RequireHostdLocked()
    {
        if (!_hostdUp)
        {
            throw Fail(ErrorCodes.HostdUnavailable, $"hostd does not answer on node {NodeId}.");
        }
    }

    private void RequireDockerLocked()
    {
        if (!_dockerUp)
        {
            throw Fail(ErrorCodes.DockerUnavailable, $"Docker does not answer on node {NodeId}.");
        }
    }

    // ---- Reads ----

    private FakeReply HostServicesList(HostServicesQuery query)
    {
        lock (_gate)
        {
            bool all = query.Include == HostServicesQuery.IncludeAll;
            return FakeReply.Json(new HostServicesResponse
            {
                HostdAvailable = _hostdUp,
                HostdError = _hostdUp ? null : "hostd does not answer.",
                Units = _hostdUp ? [.. _units.Values.OrderBy(u => u.Name, StringComparer.Ordinal).Select(u => u.ToHostUnit())] : [],
                Discovered = all && _hostdUp
                    ? [.. _units.Values.Concat(_discovered.Where(d => !_units.Values.Any(u => u.Unit == d.Unit))).Select(u => u.ToDiscovered(DeniedBy(u.Unit)))]
                    : null,
                DiscoveryErrors = all ? [] : null,
                DenyPatterns = all ? DenyPatterns : null,
                ContainersAvailable = _dockerUp,
                ContainersError = _dockerUp ? null : "Docker does not answer.",
                Containers = _dockerUp ? [.. _containers.Values.Where(c => !IsDeniedContainer(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal)] : [],
            });
        }
    }

    private FakeReply GpuProcesses()
    {
        lock (_gate)
        {
            RequireHostdLocked();
            return FakeReply.Json<IReadOnlyList<GpuProcess>>([.. GpuProcessesLocked()]);
        }
    }

    private IEnumerable<GpuProcess> GpuProcessesLocked() =>
        _instances.Values.Where(i => i.State == ModelState.Ready)
            .Select((i, n) => new GpuProcess(5000 + n, "python3", (long)(i.BudgetGiB * 1024), i.InstanceId, null));

    private FakeReply ContainerGet(string name)
    {
        lock (_gate)
        {
            if (IsDeniedContainer(name))
            {
                throw Fail(ErrorCodes.Denied, $"The agent does not manage '{name}'.");
            }

            RequireDockerLocked();
            return _containers.TryGetValue(name, out var info)
                ? FakeReply.Json(info)
                : throw Fail(ErrorCodes.NotFound, $"No container '{name}' on node {NodeId}.");
        }
    }

    private FakeReply BackupMountGet()
    {
        lock (_gate)
        {
            RequireHostdLocked();
            return FakeReply.Json(_backup);
        }
    }

    private FakeReply BackupMountTest(BackupMountTestRequest request)
    {
        lock (_gate)
        {
            RequireHostdLocked();
            var settings = request.Settings;
            bool ok = !string.IsNullOrWhiteSpace(settings.Server) && !string.IsNullOrWhiteSpace(settings.Share);
            return FakeReply.Json(new BackupMountTestResult(ok, ok ? $"Reached {settings.Server}/{settings.Share}." : "Server and share are required."));
        }
    }

    // ---- Mutations ----

    private FakeReply UnitAction(FakeCall call, HostServiceAction action)
    {
        var request = call.Request<HostServiceActionRequest>();
        return HostMutation(call, usesHostd: true, () =>
        {
            RequireHostdLocked();
            var unit = UnitLocked(call.Target);
            if (unit.Protected && action != HostServiceAction.Start && !string.Equals(request.Confirm, unit.Name, StringComparison.Ordinal))
            {
                throw Fail(ErrorCodes.ConfirmationRequired, $"'{unit.Name}' is protected: confirm with its name to {action.ToString().ToLowerInvariant()} it.");
            }
        });
    }

    private FakeReply UnitAutostart(FakeCall call)
    {
        var request = call.Request<HostServiceAutostartRequest>();
        return HostMutation(call, usesHostd: true, () =>
        {
            RequireHostdLocked();
            var unit = UnitLocked(call.Target);
            if (unit.Protected && !request.Enabled && !string.Equals(request.Confirm, unit.Name, StringComparison.Ordinal))
            {
                throw Fail(ErrorCodes.ConfirmationRequired, $"'{unit.Name}' is protected: confirm with its name to disable it at boot.");
            }
        });
    }

    private FakeReply WhitelistAdd(FakeCall call)
    {
        var request = call.Request<HostWhitelistAddRequest>();
        return HostMutation(call, usesHostd: true, () =>
        {
            RequireHostdLocked();
            if (DeniedBy(request.Unit) is { } pattern)
            {
                throw Fail(ErrorCodes.Denied, $"{request.Unit} is a system service (matches '{pattern}') and cannot be managed.");
            }

            string name = DeriveName(request.Unit);
            if (!string.Equals(request.Confirm, name, StringComparison.Ordinal))
            {
                throw Fail(ErrorCodes.ConfirmationRequired, $"Confirm with the service name '{name}' to manage it.");
            }

            if (_units.Values.Any(u => u.Unit == request.Unit) || _units.ContainsKey(name))
            {
                throw Fail(ErrorCodes.Conflict, $"{request.Unit} is already managed.");
            }

            if (!_discovered.Any(d => d.Unit == request.Unit))
            {
                throw Fail(ErrorCodes.NotFound, $"{request.Unit} is not a loaded service with a unit file.");
            }
        });
    }

    private FakeReply WhitelistUpdate(FakeCall call)
    {
        var request = call.Request<HostWhitelistUpdateRequest>();
        return HostMutation(call, usesHostd: true, () =>
        {
            RequireHostdLocked();
            var unit = UnitLocked(call.Target);
            if (unit.Protected && request.Protected == false && !string.Equals(request.Confirm, unit.Name, StringComparison.Ordinal))
            {
                throw Fail(ErrorCodes.ConfirmationRequired, $"Confirm with the service name '{unit.Name}' to remove its protection.");
            }
        });
    }

    private FakeReply WhitelistRemove(FakeCall call) => HostMutation(call, usesHostd: true, () =>
    {
        RequireHostdLocked();
        UnitLocked(call.Target);
    });

    private FakeReply ContainerAction(FakeCall call, HostServiceAction action) => HostMutation(call, usesHostd: false, () =>
    {
        if (IsDeniedContainer(call.Target))
        {
            throw Fail(ErrorCodes.Denied, $"The agent does not {action.ToString().ToLowerInvariant()} '{call.Target}' (its own, the master's or a model container).");
        }

        RequireDockerLocked();
        if (!_containers.ContainsKey(call.Target))
        {
            throw Fail(ErrorCodes.NotFound, $"No container '{call.Target}' on node {NodeId}.");
        }
    });

    private FakeReply ContainerAutostart(FakeCall call)
    {
        var request = call.Request<HostContainerAutostartRequest>();
        return HostMutation(call, usesHostd: false, () =>
        {
            if (IsDeniedContainer(call.Target))
            {
                throw Fail(ErrorCodes.Denied, $"The agent does not manage '{call.Target}'.");
            }

            RequireDockerLocked();
            if (!_containers.ContainsKey(call.Target))
            {
                throw Fail(ErrorCodes.NotFound, $"No container '{call.Target}' on node {NodeId}.");
            }

            if (!RestartPolicies.Contains(request.RestartPolicy, StringComparer.Ordinal))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"Restart policy must be one of {string.Join(", ", RestartPolicies)}.");
            }
        });
    }

    private FakeReply BackupMountSet(FakeCall call)
    {
        var settings = call.Request<BackupMountSetRequest>().Settings;
        return HostMutation(call, usesHostd: true, () =>
        {
            RequireHostdLocked();
            if (settings.Type is not ("smb" or "nfs") || string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Share))
            {
                throw Fail(ErrorCodes.InvalidRequest, "A backup share needs type smb or nfs, a server and a share.");
            }
        });
    }

    private FakeReply BackupMountRemove(FakeCall call) => HostMutation(call, usesHostd: true, RequireHostdLocked);

    /// <summary>
    /// A synchronous host-service operation: deterministic refusals first (nothing recorded), then durable acceptance, the
    /// <c>dispatched</c> stage, the simulated hostd/Docker call and the terminal record; 200 with the operation.
    /// </summary>
    private FakeReply HostMutation(FakeCall call, bool usesHostd, Action precheck)
    {
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            precheck();
            var op = AcceptLocked(call);
            StageLocked(op, OperationStages.Dispatched);
            if (usesHostd && _hostdTimesOut)
            {
                var error = Error(ErrorCodes.OutcomeUnknown, $"hostd did not answer within 60 s; the outcome of {op.Kind} on '{op.Target}' is unknown.");
                CompleteLocked(op, OperationState.Unknown, error);
                throw new AgentException(StatusOf(ErrorCodes.OutcomeUnknown), error);
            }

            object? result = ApplyHostLocked(op.Kind, op.Target, call.Redacted);
            if (_crashed)
            {
                // The host completed the action, the agent died before recording it (§4.4 item 8).
                return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
            }

            CompleteLocked(op, OperationState.Succeeded, result: result);
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    /// <summary>Applies a host action (idempotent for desired-state kinds) and returns the route's result.</summary>
    private object? ApplyHostLocked(string kind, string target, JsonElement payload)
    {
        switch (kind)
        {
            case OperationKinds.ServiceStart or OperationKinds.ServiceRestart:
            {
                var unit = UnitLocked(target);
                unit.ActiveState = "active";
                unit.SubState = "running";
                if (kind == OperationKinds.ServiceRestart || unit.MainPid is null)
                {
                    unit.MainPid = _nextPid++;
                    unit.ActiveSince = Now.ToUnixTimeSeconds();
                }

                return unit.ToHostUnit();
            }

            case OperationKinds.ServiceStop:
            {
                var unit = UnitLocked(target);
                unit.ActiveState = "inactive";
                unit.SubState = "dead";
                unit.MainPid = null;
                unit.ActiveSince = null;
                return unit.ToHostUnit();
            }

            case OperationKinds.ServiceAutostart:
            {
                var unit = UnitLocked(target);
                unit.UnitFileState = payload.Deserialize<HostServiceAutostartRequest>(AgentJson.Options)!.Enabled ? "enabled" : "disabled";
                return unit.ToHostUnit();
            }

            case OperationKinds.WhitelistAdd:
            {
                var request = payload.Deserialize<HostWhitelistAddRequest>(AgentJson.Options)!;
                var found = _discovered.First(d => d.Unit == request.Unit);
                var unit = new FakeUnit(DeriveName(request.Unit), request.Unit, found.Description)
                {
                    Managed = true,
                    Protected = request.Protected,
                    DisplayName = request.DisplayName ?? DeriveName(request.Unit),
                    Scope = request.Scope,
                };
                AddUnitLocked(unit);
                return unit.ToHostUnit();
            }

            case OperationKinds.WhitelistUpdate:
            {
                var request = payload.Deserialize<HostWhitelistUpdateRequest>(AgentJson.Options)!;
                var unit = UnitLocked(target);
                unit.DisplayName = request.DisplayName ?? unit.DisplayName;
                unit.Protected = request.Protected ?? unit.Protected;
                return unit.ToHostUnit();
            }

            case OperationKinds.WhitelistRemove:
                _units.Remove(target);
                return null;

            case OperationKinds.ContainerStart or OperationKinds.ContainerRestart:
                return _containers[target] = _containers[target] with { State = "running", Status = "Up", StartedAt = Now, ExitCode = null };

            case OperationKinds.ContainerStop:
                return _containers[target] = _containers[target] with { State = "exited", Status = "Exited (0)", ExitCode = 0 };

            case OperationKinds.ContainerAutostart:
                return _containers[target] = _containers[target] with
                {
                    RestartPolicy = payload.Deserialize<HostContainerAutostartRequest>(AgentJson.Options)!.RestartPolicy,
                };

            case OperationKinds.BackupMountSet:
            {
                var settings = payload.Deserialize<BackupMountSetRequest>(AgentJson.Options)!.Settings;
                _backup = UnconfiguredBackup() with
                {
                    Configured = true,
                    Mounted = true,
                    Type = settings.Type,
                    Server = settings.Server,
                    Share = settings.Share,
                    Subfolder = settings.Subfolder,
                    Username = settings.Username,
                    Domain = settings.Domain,
                    Version = settings.SmbVersion ?? settings.NfsVersion,
                    Source = $"//{settings.Server}/{settings.Share}",
                    MountedSource = $"//{settings.Server}/{settings.Share}",
                    MountedType = settings.Type == "smb" ? "cifs" : "nfs",
                    TotalBytes = 4L << 40,
                    FreeBytes = 3L << 40,
                    ConfiguredAt = Now,
                };
                return _backup;
            }

            case OperationKinds.BackupMountRemove:
                _backup = UnconfiguredBackup();
                return _backup;

            default:
                throw new InvalidOperationException("Not a host-service kind: " + kind);
        }
    }

    /// <summary>
    /// Recovery of a host-service operation after a restart (design §4.4 item 8): dispatched desired-state actions are
    /// re-applied (twice = once) and Succeeded; dispatched restarts and backup-mount PUTs end Unknown with the observed
    /// state and are never replayed; operations that never reached <c>dispatched</c> are run now.
    /// </summary>
    private void ReconcileHostOperationLocked(FakeOp op)
    {
        if (op.Stage == OperationStages.Dispatched && OperationKinds.Unverifiable.Contains(op.Kind))
        {
            string observed = op.Kind switch
            {
                OperationKinds.ServiceRestart when _units.TryGetValue(op.Target, out var unit) => $"{unit.ActiveState} ({unit.SubState})",
                OperationKinds.ContainerRestart when _containers.TryGetValue(op.Target, out var container) => container.State,
                OperationKinds.BackupMountSet => _backup.Mounted ? "mounted " + _backup.Source : "not mounted",
                _ => "unknown",
            };
            CompleteLocked(op, OperationState.Unknown, Error(ErrorCodes.OutcomeUnknown,
                $"Outcome unknown: the agent restarted after dispatching {op.Kind} on '{op.Target}'. Observed: {observed}. Check it and run the action again if needed."));
            return;
        }

        try
        {
            object? result = ApplyHostLocked(op.Kind, op.Target, op.Payload);
            CompleteLocked(op, OperationState.Succeeded, result: result);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or AgentException)
        {
            CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.InternalError, ex.Message));
        }
    }

    private FakeUnit UnitLocked(string name) =>
        _units.TryGetValue(name, out var unit) ? unit : throw Fail(ErrorCodes.NotFound, $"'{name}' is not a managed service on node {NodeId}.");

    /// <summary>hostd's name rule (<c>HostdConfig.DeriveName</c>): lower-case unit stem, other characters as dashes.</summary>
    private static string DeriveName(string unit)
    {
        string stem = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
        string name = new string([.. stem.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-')]).Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        return name.Length == 0 ? "unit" : name;
    }

    private sealed class FakeUnit(string name, string unit, string description)
    {
        public string Name { get; } = name;

        public string Unit { get; } = unit;

        public string Description { get; } = description;

        public string Scope { get; set; } = "system";

        public string DisplayName { get; set; } = name;

        public bool Protected { get; set; }

        public bool Managed { get; set; }

        public string ActiveState { get; set; } = "inactive";

        public string SubState { get; set; } = "dead";

        public string UnitFileState { get; set; } = "enabled";

        public int? MainPid { get; set; }

        public long? ActiveSince { get; set; }

        public HostUnit ToHostUnit() => new()
        {
            Name = Name,
            Unit = Unit,
            Scope = Scope,
            DisplayName = DisplayName,
            Description = Description,
            Protected = Protected,
            ActiveState = ActiveState,
            SubState = SubState,
            UnitFileState = UnitFileState,
            MainPid = MainPid,
            ActiveSinceUnix = ActiveSince,
            MemoryBytes = MainPid is null ? null : 64L * 1024 * 1024,
        };

        public HostDiscoveredUnit ToDiscovered(string? deniedBy) => new()
        {
            Name = Name,
            Unit = Unit,
            Scope = Scope,
            Description = Description,
            ActiveState = ActiveState,
            SubState = SubState,
            UnitFileState = UnitFileState,
            ActiveSinceUnix = ActiveSince,
            Managed = Managed,
            Denied = deniedBy is not null,
            DeniedBy = deniedBy,
        };
    }
}
