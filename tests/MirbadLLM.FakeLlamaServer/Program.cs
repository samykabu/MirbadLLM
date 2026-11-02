using System.Collections;

namespace MirbadLLM.FakeLlamaServer;

/// <summary>
/// Entry point of the fake llama-server (P3; created by MN-202, implemented by MN-211): runs <see cref="FakeLlamaServer"/>
/// with the process's own arguments and environment, logs to stderr only, and exits with the code the server ends with
/// (a crash knob, an argument error = 1, Ctrl+C = 0).
/// </summary>
public static class FakeLlamaServerProgram
{
    /// <summary>Exit code of an argument error (as llama-server).</summary>
    public const int ArgumentErrorExitCode = FakeLlamaOptions.ArgumentErrorExitCode;

    /// <summary>Runs the fake server until it exits.</summary>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        await using var server = await FakeLlamaServer.StartAsync(args, environment, Console.Error).ConfigureAwait(false);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var stopped = Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => 0, TaskScheduler.Default);
        var done = await Task.WhenAny(server.Exited, stopped).ConfigureAwait(false);
        if (done == stopped)
        {
            await server.ExitAsync(0).ConfigureAwait(false);
        }

        return await server.Exited.ConfigureAwait(false);
    }
}
