using System.Globalization;

namespace MirbadLLM.FakeLlamaServer;

/// <summary>
/// What the fake llama-server was started with: the llama-server arguments it reads (<c>--host</c>, <c>--port</c>,
/// <c>--alias</c>, <c>-c</c>, <c>-np</c>, <c>--embedding</c>, <c>-m</c>) and the test knobs from its environment. Every
/// other argument, including the rest of <c>LlamaCppFlags.AlwaysArgs</c> (<c>--metrics</c>, <c>--no-ui</c>,
/// <c>--cache-ram 0</c>, <c>--fit off</c>), is accepted and ignored, as the real server would accept it.
/// </summary>
public sealed record FakeLlamaOptions
{
    /// <summary>Environment variable: milliseconds <c>/health</c> answers 503 "Loading model" (default 300).</summary>
    public const string LoadMsVariable = "FAKE_LLAMA_LOAD_MS";

    /// <summary>Environment variable: exit this many milliseconds after start (a crash; before Ready when below the load time).</summary>
    public const string ExitAfterMsVariable = "FAKE_LLAMA_EXIT_AFTER_MS";

    /// <summary>Environment variable: exit this many milliseconds after the model is loaded (a crash after Ready).</summary>
    public const string ExitAfterReadyMsVariable = "FAKE_LLAMA_EXIT_AFTER_READY_MS";

    /// <summary>Environment variable: exit code of a knob-driven exit (default 1).</summary>
    public const string ExitCodeVariable = "FAKE_LLAMA_EXIT_CODE";

    /// <summary>
    /// Environment variable: when "1", the warm-up logs a Metal out-of-memory failure (as llama-server does when its
    /// buffers do not fit the GPU) and the server still answers <c>/health</c> 200, as the real one does.
    /// </summary>
    public const string GpuOutOfMemoryVariable = "FAKE_LLAMA_GPU_OOM";

    /// <summary>Environment variable: native embedding length (default 16).</summary>
    public const string EmbeddingLengthVariable = "FAKE_LLAMA_N_EMBD";

    /// <summary>Environment variable: llama-server's API key; when set, everything but <c>/health</c> needs it.</summary>
    public const string ApiKeyVariable = "LLAMA_API_KEY";

    /// <summary>Exit code of an argument error (as llama-server: 1).</summary>
    public const int ArgumentErrorExitCode = 1;

    /// <summary>Bind address (<c>--host</c>, default 127.0.0.1).</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Port (<c>--port</c>, default 8080 like llama-server).</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Served name (<c>--alias</c>/<c>-a</c>); null = the model file name.</summary>
    public string? Alias { get; init; }

    /// <summary>Model file (<c>-m</c>/<c>--model</c>).</summary>
    public string? ModelFile { get; init; }

    /// <summary>Total context (<c>-c</c>, default 4096).</summary>
    public int ContextTotal { get; init; } = 4096;

    /// <summary>Slots (<c>-np</c>, default 1).</summary>
    public int Parallel { get; init; } = 1;

    /// <summary><c>--embedding</c>/<c>--embeddings</c>.</summary>
    public bool Embedding { get; init; }

    /// <summary>The API key, or null (open server).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Loading time.</summary>
    public TimeSpan LoadTime { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Exit after start, or null.</summary>
    public TimeSpan? ExitAfter { get; init; }

    /// <summary>Exit after the model is loaded, or null.</summary>
    public TimeSpan? ExitAfterReady { get; init; }

    /// <summary>Exit code of a knob-driven exit.</summary>
    public int ExitCode { get; init; } = 1;

    /// <summary>The warm-up logs a GPU out-of-memory failure.</summary>
    public bool GpuOutOfMemory { get; init; }

    /// <summary>Native embedding length.</summary>
    public int EmbeddingLength { get; init; } = 16;

    /// <summary>The name <c>/v1/models</c> lists.</summary>
    public string ServedName => Alias ?? Path.GetFileName(ModelFile ?? "model.gguf");

    /// <summary>Tokens per slot (<c>-c / -np</c>, S1: <c>n_ctx_slot = c / np</c>).</summary>
    public int SlotContext => Math.Max(1, ContextTotal / Math.Max(1, Parallel));

    /// <summary>
    /// Reads <paramref name="args"/> and <paramref name="environment"/>; throws <see cref="ArgumentException"/> with
    /// llama-server's wording for a flag that misses its value or a number that does not parse.
    /// </summary>
    public static FakeLlamaOptions Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var options = new FakeLlamaOptions
        {
            ApiKey = environment.TryGetValue(ApiKeyVariable, out string? key) && key.Length > 0 ? key : null,
            LoadTime = Milliseconds(environment, LoadMsVariable) ?? TimeSpan.FromMilliseconds(300),
            ExitAfter = Milliseconds(environment, ExitAfterMsVariable),
            ExitAfterReady = Milliseconds(environment, ExitAfterReadyMsVariable),
            ExitCode = Integer(environment, ExitCodeVariable) ?? 1,
            EmbeddingLength = Integer(environment, EmbeddingLengthVariable) ?? 16,
            GpuOutOfMemory = environment.TryGetValue(GpuOutOfMemoryVariable, out string? oom) && oom == "1",
        };

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--host":
                    options = options with { Host = Value(args, ref i) };
                    break;
                case "--port":
                    options = options with { Port = Number(args, ref i) };
                    break;
                case "--alias" or "-a":
                    options = options with { Alias = Value(args, ref i) };
                    break;
                case "-m" or "--model":
                    options = options with { ModelFile = Value(args, ref i) };
                    break;
                case "-c" or "--ctx-size":
                    options = options with { ContextTotal = Number(args, ref i) };
                    break;
                case "-np" or "--parallel":
                    options = options with { Parallel = Number(args, ref i) };
                    break;
                case "--embedding" or "--embeddings":
                    options = options with { Embedding = true };
                    break;
                default:
                    // Accepted and ignored (--metrics, --no-ui, --fit off, --cache-ram 0, -ngl 999, ...): a value that
                    // follows an ignored flag is skipped with it.
                    if (arg.StartsWith('-') && i + 1 < args.Count && !LooksLikeFlag(args[i + 1]))
                    {
                        i++;
                    }

                    break;
            }
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"error while handling argument \"--port\": invalid port {options.Port}");
        }

        return options;
    }

    private static bool LooksLikeFlag(string value) =>
        value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1]);

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"error while handling argument \"{args[i]}\": expected value for argument");
        }

        return args[++i];
    }

    private static int Number(IReadOnlyList<string> args, ref int i)
    {
        string flag = args[i];
        string value = Value(args, ref i);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new ArgumentException($"error while handling argument \"{flag}\": invalid value '{value}'");
    }

    private static TimeSpan? Milliseconds(IReadOnlyDictionary<string, string> environment, string name) =>
        Integer(environment, name) is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static int? Integer(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.TryGetValue(name, out string? text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
}
