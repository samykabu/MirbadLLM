using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// What a SGLang or NIM container was launched with, as a definition to compare with the saved one. Without it these
/// models went through the vLLM parser, and the Apply dialog listed changes nobody made. Every other setting stays the
/// saved value, because these servers don't take it.
/// </summary>
public static class ContainerRuntimeArgsParser
{
    /// <summary>Reads <paramref name="launch"/> onto <paramref name="template"/>, the saved definition.</summary>
    public static ModelDefinition Parse(LaunchConfig launch, ModelDefinition template)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(template);
        var launched = template with
        {
            Runtime = launch.Runtime,
            Container = new ContainerRuntimeSettings
            {
                Image = launch.Image ?? "",
                ImageDigest = launch.ImageDigest,
                // NIM's Cmd can be the image's own command, which the spec never set, so it can't show what was asked for.
                ExtraArgs = template.Container?.ExtraArgs ?? [],
            },
        };
        return launch.Runtime == RuntimeKind.Sglang ? ParseSglang(launch.Argv, launched) : launched;
    }

    private static ModelDefinition ParseSglang(IReadOnlyList<string> argv, ModelDefinition model)
    {
        string? algorithm = ValueOf(argv, "--speculative-algorithm");
        string[] owned =
        [
            "--model-path", "--served-model-name", "--host", "--port", "--speculative-algorithm", "--speculative-draft-model-path",
            .. algorithm is null ? Array.Empty<string>() : [SglangSpeculativeArgs.DepthFlag(algorithm)],
        ];

        var extra = new List<string>();
        int start = argv.ToList().IndexOf("sglang.launch_server") + 1;
        for (int i = start; i < argv.Count; i++)
        {
            if (owned.Contains(argv[i], StringComparer.Ordinal))
            {
                i++;
            }
            else
            {
                extra.Add(argv[i]);
            }
        }

        string prefix = LogicalVolumes.ModelsTarget + "/";
        string? path = ValueOf(argv, "--model-path");
        return model with
        {
            ServedName = ValueOf(argv, "--served-model-name") ?? model.ServedName,
            ModelPath = path is not null && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : model.ModelPath,
            Speculative = algorithm is null ? null : ParseSpeculative(argv, algorithm, model.Speculative),
            Container = model.Container! with { ExtraArgs = extra },
        };
    }

    private static SpeculativeDecoding ParseSpeculative(IReadOnlyList<string> argv, string algorithm, SpeculativeDecoding? saved)
    {
        // The builder upper-cases the algorithm, so "dflash" saved and DFLASH launched is no change.
        string method = string.Equals(saved?.Method, algorithm, StringComparison.OrdinalIgnoreCase) ? saved!.Method : algorithm;
        int tokens = int.TryParse(ValueOf(argv, SglangSpeculativeArgs.DepthFlag(algorithm)), out int depth) ? depth : 0;
        return new SpeculativeDecoding(method, tokens) { DraftModel = ValueOf(argv, "--speculative-draft-model-path") };
    }

    private static string? ValueOf(IReadOnlyList<string> argv, string flag)
    {
        for (int i = 0; i + 1 < argv.Count; i++)
        {
            if (string.Equals(argv[i], flag, StringComparison.Ordinal))
            {
                return argv[i + 1];
            }
        }

        return null;
    }
}
