using MirbadLLM.Agent.Hosting;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// Settings of the SCM host services (MN-228, design-p3p4 §A.4.3). The composition (MN-232) sets
/// <see cref="WhitelistPath"/> from the agent's state directory (<see cref="ForStateDirectory"/>); the rest has working
/// defaults.
/// </summary>
public sealed class ScmHostServicesOptions
{
    /// <summary>The whitelist file (<c>{state}\scm-whitelist.json</c>, <see cref="ScmWhitelist.FileName"/>).</summary>
    public string WhitelistPath { get; set; } = "";

    /// <summary>
    /// How long a start, stop or restart waits for the service to reach its state (hostd bounds its actions at 60 s, the
    /// same here). A service still pending after that ends the operation <c>Unknown</c> with the observed state (D42).
    /// </summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the state is read while waiting.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The agent's own service, always refused (besides <see cref="WindowsServiceHosting.ServiceName"/>, which is compiled in).</summary>
    public string AgentServiceName { get; set; } = WindowsServiceHosting.ServiceName;

    /// <summary>Options with the whitelist in <paramref name="stateDirectory"/>.</summary>
    public static ScmHostServicesOptions ForStateDirectory(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new ScmHostServicesOptions { WhitelistPath = Path.Combine(stateDirectory, ScmWhitelist.FileName) };
    }
}
