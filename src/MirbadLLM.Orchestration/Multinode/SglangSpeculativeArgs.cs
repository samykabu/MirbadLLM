using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Turns <see cref="ModelDefinition.Speculative"/> into <c>sglang.launch_server</c> flags
/// (https://docs.sglang.io/advanced_features/speculative_decoding.html). The method is the algorithm, the draft model is
/// a folder under <c>/models</c>, and the token count is the depth flag that algorithm uses. Rarer flags such as
/// <c>--speculative-eagle-topk</c> stay in the container's extra arguments.
/// </summary>
public static class SglangSpeculativeArgs
{
    /// <summary>The algorithms the model editor offers; <c>UNO</c> also needs a LoRA path, so it stays a typed-in value.</summary>
    public static IReadOnlyList<string> Algorithms { get; } = ["DFLASH", "EAGLE", "EAGLE3", "STANDALONE", "NGRAM"];

    /// <summary>The flags of <paramref name="speculative"/>, ready to go before the extra arguments.</summary>
    public static IReadOnlyList<string> Build(SpeculativeDecoding speculative)
    {
        ArgumentNullException.ThrowIfNull(speculative);
        string algorithm = speculative.Method.ToUpperInvariant();
        return
        [
            "--speculative-algorithm", algorithm,
            .. speculative.DraftModel is { } draft ? ["--speculative-draft-model-path", draft] : Array.Empty<string>(),
            DepthFlag(algorithm), speculative.NumSpeculativeTokens.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>Why the speculative settings of a SGLang model can't be used; null when they can.</summary>
    public static string? Problem(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Speculative is not { } speculative)
        {
            return null;
        }

        if (string.IsNullOrEmpty(speculative.Method) || !speculative.Method.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return "SGLang speculative decoding needs an algorithm, such as DFLASH or EAGLE3.";
        }

        if (speculative.NumSpeculativeTokens < 1)
        {
            return "SGLang speculative decoding needs a token count of at least 1.";
        }

        if (!speculative.Extra.IsEmpty)
        {
            return "SGLang has no speculative config object; pass other speculative flags as extra arguments.";
        }

        string prefix = LogicalVolumes.ModelsTarget + "/";
        if (speculative.DraftModel is { } draft &&
            (!draft.StartsWith(prefix, StringComparison.Ordinal) || !LlamaCppArgsBuilder.IsRelativePath(draft[prefix.Length..])))
        {
            return $"The draft model '{draft}' must be a folder under {LogicalVolumes.ModelsTarget}, such as {prefix}my-drafter.";
        }

        // SGLang would keep whichever repeated flag comes last, so the saved setting could silently lose.
        string[] owned = [.. Build(speculative).Where(a => a.StartsWith("--", StringComparison.Ordinal)), "--speculative-dflash-block-size"];
        foreach (string arg in model.Container?.ExtraArgs ?? [])
        {
            if (owned.Any(flag => arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal)))
            {
                return $"Extra argument '{arg}' repeats a speculative decoding setting; remove it or turn speculative decoding off.";
            }
        }

        return null;
    }

    /// <summary>The flag that sets how much <paramref name="algorithm"/> (upper case) drafts per step.</summary>
    internal static string DepthFlag(string algorithm) => algorithm is "EAGLE" or "EAGLE3" or "STANDALONE" or "NEXTN"
        ? "--speculative-num-steps"
        : "--speculative-num-draft-tokens";
}
