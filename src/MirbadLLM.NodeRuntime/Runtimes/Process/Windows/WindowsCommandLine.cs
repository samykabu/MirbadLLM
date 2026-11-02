using System.Runtime.Versioning;
using System.Text;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

/// <summary>
/// The one quoting function of the Windows process host (design-p3p4 §A.1.3, <see cref="Abstractions.ProcessHostStart.Arguments"/>)
/// and its inverse. <see cref="Build"/> produces a command line that <c>CommandLineToArgvW</c> (and the MSVC CRT, which
/// llama-server uses) splits back into exactly the given arguments; <see cref="Split"/> calls <c>CommandLineToArgvW</c>.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>
    /// <c>argv[0]</c> (the executable path, always quoted: <c>CommandLineToArgvW</c> reads <c>argv[0]</c> without escape
    /// processing, so it must not contain a quote) followed by each argument, quoted when it is empty or contains a space,
    /// tab, newline or quote; backslashes are doubled only before a quote and before the closing quote.
    /// </summary>
    public static string Build(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executablePath.Contains('"', StringComparison.Ordinal) || executablePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path must not contain a quote or a NUL character.", nameof(executablePath));
        }

        var sb = new StringBuilder(executablePath.Length + 2 + (arguments.Count * 16));
        sb.Append('"').Append(executablePath).Append('"');
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            if (argument.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("An argument must not contain a NUL character.", nameof(arguments));
            }

            sb.Append(' ');
            AppendArgument(sb, argument);
        }

        return sb.ToString();
    }

    /// <summary>Splits a command line with <c>CommandLineToArgvW</c>; the result includes <c>argv[0]</c>.</summary>
    [SupportedOSPlatform("windows")]
    public static unsafe IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        fixed (char* p = commandLine)
        {
            char** argv = WindowsNative.CommandLineToArgvW(p, out int argc);
            if (argv == null)
            {
                throw WindowsNative.LastError("CommandLineToArgvW");
            }

            try
            {
                var result = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    result[i] = new string(argv[i]);
                }

                return result;
            }
            finally
            {
                WindowsNative.LocalFree((nint)argv);
            }
        }
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 2n backslashes + \" → n backslashes and a literal quote.
                sb.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        // Backslashes before the closing quote are doubled so the quote stays a delimiter.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
