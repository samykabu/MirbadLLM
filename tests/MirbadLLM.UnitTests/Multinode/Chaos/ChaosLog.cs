using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// Captures every log entry of the master and the agent compositions: the formatted message, every structured value and
/// the exception text, so a secret passed as a log argument is found even when the template does not print it.
/// </summary>
internal sealed class ChaosLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public int Count => _entries.Count;

    public IEnumerable<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>The entries containing <paramref name="secret"/>.</summary>
    public List<string> Containing(string secret) => [.. _entries.Where(e => e.Contains(secret, StringComparison.Ordinal))];

    private sealed class Logger(ChaosLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner._entries.Enqueue($"[scope] {category}: {Describe(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new StringBuilder();
            entry.Append(logLevel).Append(' ').Append(category).Append(": ").Append(formatter(state, exception));
            entry.Append(" | ").Append(Describe(state));
            if (exception is not null)
            {
                entry.Append(" | ").Append(exception);
            }

            owner._entries.Enqueue(entry.ToString());
        }

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))
                : state?.ToString() ?? "";
    }
}
