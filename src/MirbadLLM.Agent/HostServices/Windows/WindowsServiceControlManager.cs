using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// <see cref="IServiceControlManager"/> over <c>advapi32</c> (MN-228; P/Invoke, no package). Every call opens the SCM
/// and the service with only the access it needs and closes them again: listing and reading need
/// <c>SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE</c> and <c>SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG</c>,
/// which every account has on most services; <see cref="RightsOn"/> only opens a handle (it never sends a control);
/// start, stop and the start type need the rights <c>install-agent.ps1 -AllowService</c> grants.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsServiceControlManager : IServiceControlManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStateAll = 0x00000003;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ScEnumProcessInfo = 0;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;

    /// <inheritdoc/>
    public bool IsAvailable
    {
        get
        {
            using var scm = OpenScm(ScManagerConnect, throwOnFailure: false);
            return scm is not null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScmService> List()
    {
        using var scm = OpenScm(ScManagerConnect | ScManagerEnumerateService, throwOnFailure: true)!;
        var result = new List<ScmService>();
        uint resume = 0;
        int size = 64 * 1024;
        while (true)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool ok = EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, buffer, (uint)size,
                    out uint needed, out uint returned, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && error != ErrorMoreData)
                {
                    throw Failure(error, "list the services");
                }

                int stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (int i = 0; i < returned; i++)
                {
                    var row = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * stride));
                    string name = Marshal.PtrToStringUni(row.ServiceName) ?? "";
                    string display = Marshal.PtrToStringUni(row.DisplayName) ?? name;
                    var (startType, description, _) = ReadConfig(scm, name);
                    result.Add(ToService(name, display, row.Status, startType, description));
                }

                if (ok)
                {
                    return result;
                }

                size = (int)Math.Max(needed, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <inheritdoc/>
    public ScmService? Find(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var scm = OpenScm(ScManagerConnect, throwOnFailure: true)!;
        using var service = OpenServiceW(scm, serviceName, ServiceQueryStatus);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ScmException.ServiceDoesNotExist)
            {
                return null;
            }

            throw Failure(error, $"open {serviceName}");
        }

        var status = QueryStatus(service, serviceName);
        if ((status.ServiceType & ServiceWin32) == 0)
        {
            return null; // a driver, not a Win32 service
        }

        var (startType, description, display) = ReadConfig(scm, serviceName);

        // The key name as the SCM spells it (the caller may have used another case).
        string canonical = display is not null && GetServiceKeyNameW(scm, display, out string? key)
            && string.Equals(key, serviceName, StringComparison.OrdinalIgnoreCase) ? key! : serviceName;
        return ToService(canonical, display ?? canonical, status, startType, description);
    }

    /// <inheritdoc/>
    public ScmRights RightsOn(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using var scm = OpenScm(ScManagerConnect, throwOnFailure: true)!;
        var rights = ScmRights.None;
        if (CanOpen(scm, serviceName, ServiceStart))
        {
            rights |= ScmRights.Start;
        }

        if (CanOpen(scm, serviceName, ServiceStop))
        {
            rights |= ScmRights.Stop;
        }

        if (CanOpen(scm, serviceName, ServiceChangeConfig))
        {
            rights |= ScmRights.ChangeConfig;
        }

        return rights;
    }

    /// <inheritdoc/>
    public void StartService(string serviceName)
    {
        using var scm = OpenScm(ScManagerConnect, throwOnFailure: true)!;
        using var service = Open(scm, serviceName, ServiceStart);
        if (!StartServiceW(service, 0, 0))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"start {serviceName}");
        }
    }

    /// <inheritdoc/>
    public void StopService(string serviceName)
    {
        using var scm = OpenScm(ScManagerConnect, throwOnFailure: true)!;
        using var service = Open(scm, serviceName, ServiceStop);
        if (!ControlService(service, ServiceControlStop, out _))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"stop {serviceName}");
        }
    }

    /// <inheritdoc/>
    public void SetStartType(string serviceName, ScmStartType startType)
    {
        if (startType is ScmStartType.Boot or ScmStartType.System)
        {
            throw new ArgumentOutOfRangeException(nameof(startType), "Boot and system start types are for drivers.");
        }

        using var scm = OpenScm(ScManagerConnect, throwOnFailure: true)!;
        using var service = Open(scm, serviceName, ServiceChangeConfig | ServiceQueryConfig);
        uint type = startType == ScmStartType.AutomaticDelayed ? (uint)ScmStartType.Automatic : (uint)startType;
        if (!ChangeServiceConfigW(service, ServiceNoChange, type, ServiceNoChange, null, null, 0, null, null, null, null))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"change the start type of {serviceName}");
        }

        if (startType is ScmStartType.Automatic or ScmStartType.AutomaticDelayed)
        {
            var delayed = new DelayedAutoStartInfo { Delayed = startType == ScmStartType.AutomaticDelayed ? 1 : 0 };
            if (!ChangeServiceConfig2W(service, ServiceConfigDelayedAutoStartInfo, ref delayed))
            {
                throw Failure(Marshal.GetLastPInvokeError(), $"change the delayed start of {serviceName}");
            }
        }
    }

    /// <inheritdoc/>
    public long? WorkingSetBytes(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? ProcessStartedAt(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ScmService ToService(string name, string display, ServiceStatusProcess status, ScmStartType? startType, string description) =>
        new(
            name,
            display,
            Enum.IsDefined((ScmServiceState)status.CurrentState) ? (ScmServiceState)status.CurrentState : ScmServiceState.Unknown,
            status.ProcessId == 0 ? null : (int)status.ProcessId,
            (status.ServiceType & ServiceWin32OwnProcess) != 0 && (status.ServiceType & 0x20) == 0,
            startType,
            description);

    private static ScmHandle? OpenScm(uint access, bool throwOnFailure)
    {
        var scm = OpenSCManagerW(null, null, access);
        if (!scm.IsInvalid)
        {
            return scm;
        }

        int error = Marshal.GetLastPInvokeError();
        scm.Dispose();
        return throwOnFailure ? throw Failure(error, "open the Service Control Manager") : null;
    }

    private static ScmHandle Open(ScmHandle scm, string serviceName, uint access)
    {
        var service = OpenServiceW(scm, serviceName, access);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            service.Dispose();
            throw Failure(error, $"open {serviceName}");
        }

        return service;
    }

    private static bool CanOpen(ScmHandle scm, string serviceName, uint access)
    {
        using var service = OpenServiceW(scm, serviceName, access);
        if (!service.IsInvalid)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ScmException.AccessDenied)
        {
            return false;
        }

        throw Failure(error, $"open {serviceName}");
    }

    private static ServiceStatusProcess QueryStatus(ScmHandle service, string serviceName)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
            {
                throw Failure(Marshal.GetLastPInvokeError(), $"read the state of {serviceName}");
            }

            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Start type, description and display name; (null, "", null) when the account may not read the configuration.</summary>
    private static (ScmStartType? StartType, string Description, string? DisplayName) ReadConfig(ScmHandle scm, string serviceName)
    {
        using var service = OpenServiceW(scm, serviceName, ServiceQueryConfig);
        if (service.IsInvalid)
        {
            return (null, "", null);
        }

        ScmStartType? startType = null;
        string? displayName = null;
        if (QueryConfig(service) is { } config)
        {
            displayName = config.DisplayName;
            startType = (ScmStartType)config.StartType;
            if (startType == ScmStartType.Automatic && QueryConfig2(service, ServiceConfigDelayedAutoStartInfo) is { } delayedBuffer)
            {
                try
                {
                    if (Marshal.ReadInt32(delayedBuffer) != 0)
                    {
                        startType = ScmStartType.AutomaticDelayed;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(delayedBuffer);
                }
            }
        }

        string description = "";
        if (QueryConfig2(service, ServiceConfigDescription) is { } descriptionBuffer)
        {
            try
            {
                description = Marshal.PtrToStringUni(Marshal.ReadIntPtr(descriptionBuffer)) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(descriptionBuffer);
            }
        }

        return (startType, description, displayName);
    }

    private static (uint StartType, string DisplayName)? QueryConfig(ScmHandle service)
    {
        QueryServiceConfigW(service, 0, 0, out uint needed);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || needed == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return null;
            }

            var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            return (config.StartType, Marshal.PtrToStringUni(config.DisplayName) ?? "");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>A <c>QueryServiceConfig2W</c> buffer the caller frees; null when it cannot be read.</summary>
    private static nint? QueryConfig2(ScmHandle service, uint level)
    {
        QueryServiceConfig2W(service, level, 0, 0, out uint needed);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || needed == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((int)needed);
        if (QueryServiceConfig2W(service, level, buffer, needed, out _))
        {
            return buffer;
        }

        Marshal.FreeHGlobal(buffer);
        return null;
    }

    private static bool GetServiceKeyNameW(ScmHandle scm, string displayName, out string? key)
    {
        uint chars = 257;
        nint buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetServiceKeyName(scm, displayName, buffer, ref chars))
            {
                key = Marshal.PtrToStringUni(buffer);
                return key is not null;
            }

            key = null;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ScmException Failure(int error, string what) =>
        new(error, $"Windows could not {what}: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");

    // ---------------------------------------------------------------- advapi32

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public nint ServiceName;
        public nint DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DelayedAutoStartInfo
    {
        public int Delayed;
    }

    /// <summary>A service or SCM handle (<c>CloseServiceHandle</c>).</summary>
    internal sealed partial class ScmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScmHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseServiceHandle(nint handle);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ScmHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ScmHandle OpenServiceW(ScmHandle scm, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(
        ScmHandle scm, int infoLevel, uint serviceType, uint serviceState, nint services, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(ScmHandle service, int infoLevel, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(ScmHandle service, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2W(ScmHandle service, uint infoLevel, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "GetServiceKeyNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetServiceKeyName(ScmHandle scm, string displayName, nint serviceName, ref uint bufferChars);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(ScmHandle service, uint argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(ScmHandle service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(
        ScmHandle service, uint serviceType, uint startType, uint errorControl, string? binaryPathName, string? loadOrderGroup,
        nint tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(ScmHandle service, uint infoLevel, ref DelayedAutoStartInfo info);
}
