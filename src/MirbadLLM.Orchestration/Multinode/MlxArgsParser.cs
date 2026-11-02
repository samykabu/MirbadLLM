using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The inverse of <see cref="MlxArgsBuilder"/> (docs/macos/implementation-plan.md, Phase 3): reads the argv an MLX instance
/// was launched with (<c>/launch-config</c>, tokens kept) back into a <see cref="ModelDefinition"/>, so the Apply dialog can
/// show "old → new". The builder's own flags become the typed settings; everything else is kept, in order, as
/// <see cref="MlxSettings.ExtraArgs"/>. What the argv cannot carry (<see cref="MlxSettings.ContextSize"/>, the folder
/// sources, tags) comes from the saved <c>template</c>, so <c>Build(Parse(Build(m)))</c> equals <c>Build(m)</c>. Pure.
/// </summary>
public static class MlxArgsParser
{
    /// <summary>
    /// Reads a <c>/launch-config</c> back: the argv, the executable version (the template's pin, or no pin when the launch
    /// used the node's default and the template pins none) and the budget (<see cref="ContainerLabels.BudgetGiB"/> label).
    /// </summary>
    public static ModelDefinition Parse(LaunchConfig launch, ModelDefinition template, NodeRecord? node = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(template);
        double budget = launch.Labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0
                ? parsed
                : template.MemoryBudgetGiB;
        var model = Parse(launch.Argv, template with { MemoryBudgetGiB = budget });
        string? saved = template.Mlx?.ExecutableVersion;
        string? launched = launch.ExecutableVersion;
        string? version = launched is null || string.Equals(launched, saved, StringComparison.Ordinal)
            ? saved
            : saved is null && node is not null &&
              string.Equals(launched, LaunchSpecBuilder.NodeDefaultExecutableVersion(node, RuntimeKind.Mlx), StringComparison.Ordinal)
                ? null
                : launched;
        return model with { Mlx = model.Mlx! with { ExecutableVersion = version } };
    }

    /// <summary>Reads a builder argv back into <paramref name="template"/>. The result is an MLX definition.</summary>
    public static ModelDefinition Parse(IReadOnlyList<string> argv, ModelDefinition template)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(template);
        var saved = template.Mlx ?? new MlxSettings { ModelFolder = string.Empty };
        var settings = saved with
        {
            AdapterFolder = null,
            DraftModelFolder = null,
            NumDraftTokens = null,
            KvBits = null,
            MaxTokens = null,
            ChatTemplateArgs = null,
            TrustRemoteCode = false,
        };
        var extra = new List<string>();
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            string? next = i + 1 < argv.Count ? argv[i + 1] : null;
            switch (arg)
            {
                case MlxFlags.Model when Folder(next) is { } folder:
                    settings = settings with { ModelFolder = folder };
                    i++;
                    break;
                case "--adapter-path" when Folder(next) is { } adapter:
                    settings = settings with { AdapterFolder = adapter };
                    i++;
                    break;
                case "--draft-model" when Folder(next) is { } draft:
                    settings = settings with { DraftModelFolder = draft };
                    i++;
                    break;
                case "--num-draft-tokens" when Int(next) is { } draftTokens:
                    settings = settings with { NumDraftTokens = draftTokens };
                    i++;
                    break;
                case "--host" or "--port" when next is not null:
                    i++; // always 127.0.0.1 and {port}
                    break;
                case "--decode-concurrency" when Int(next) is { } parallel:
                    settings = settings with { Parallel = parallel };
                    i++;
                    break;
                case "--prompt-concurrency" when Int(next) is not null:
                    i++; // derived from --decode-concurrency
                    break;
                case "--prompt-cache-bytes" when next is not null &&
                    long.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes):
                    settings = settings with
                    {
                        PromptCacheGiB = MlxArgsBuilder.PromptCacheBytes(saved) == bytes ? saved.PromptCacheGiB : bytes / 1_073_741_824.0,
                    };
                    i++;
                    break;
                case "--kv-bits" when Int(next) is { } kvBits:
                    settings = settings with { KvBits = kvBits };
                    i++;
                    break;
                case "--max-tokens" when Int(next) is { } maxTokens:
                    settings = settings with { MaxTokens = maxTokens };
                    i++;
                    break;
                case "--chat-template-args" when next is not null:
                    // The template's own text when it is the same object (the builder emits it compacted).
                    settings = settings with
                    {
                        ChatTemplateArgs = saved.ChatTemplateArgs is { } kept && SameJson(kept, next) ? kept : next,
                    };
                    i++;
                    break;
                case "--trust-remote-code":
                    settings = settings with { TrustRemoteCode = true };
                    break;
                default:
                    extra.Add(arg);
                    break;
            }
        }

        return template with
        {
            Runtime = RuntimeKind.Mlx,
            LlamaCpp = null,
            Mlx = settings with { ExtraArgs = extra },
        };
    }

    private static string? Folder(string? value)
    {
        string prefix = ProcessArgvTokens.Models + "/";
        return value is not null && value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : null;
    }

    private static int? Int(string? value) =>
        value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

    private static bool SameJson(string a, string b)
    {
        try
        {
            using var left = System.Text.Json.JsonDocument.Parse(a);
            using var right = System.Text.Json.JsonDocument.Parse(b);
            return System.Text.Json.JsonSerializer.Serialize(left.RootElement) == System.Text.Json.JsonSerializer.Serialize(right.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
