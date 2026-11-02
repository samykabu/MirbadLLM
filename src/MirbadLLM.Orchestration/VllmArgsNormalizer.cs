using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>A raw flag moved out of <see cref="ModelDefinition.ExtraArgs"/> into its typed field.</summary>
/// <param name="Flag">The flag as written, e.g. <c>--speculative-config</c> or <c>-cc.cudagraph_mode</c>.</param>
/// <param name="Field">The editor's name for the field it now lives in, e.g. "Speculative decoding".</param>
public sealed record LiftedArg(string Flag, string Field)
{
    public override string ToString() => $"{Flag} → {Field}";
}

/// <summary>The outcome of <see cref="VllmArgsNormalizer.Normalize"/>.</summary>
/// <param name="Definition">The canonical definition (the same instance when nothing changed).</param>
/// <param name="Lifted">Flags moved from ExtraArgs into typed fields, in order.</param>
/// <param name="Problems">Owned flags that could not be read (missing or invalid value); they stay in ExtraArgs and a save refuses them.</param>
public sealed record NormalizedDefinition(ModelDefinition Definition, IReadOnlyList<LiftedArg> Lifted, IReadOnlyList<string> Problems);

/// <summary>
/// Canonical effective configuration. The settings mirbadllm owns — <c>--speculative-config</c>,
/// <c>--compilation-config</c> (<c>-cc</c>), <c>--max-num-seqs</c>, <c>--max-num-batched-tokens</c>,
/// <c>--kv-cache-dtype</c>, <c>--[no-]enable-prefix-caching</c>, <c>--[no-]async-scheduling</c> and
/// <c>--mamba-cache-mode</c> — live only in typed <see cref="ModelDefinition"/> fields. This lifts any of them found
/// in <see cref="ModelDefinition.ExtraArgs"/> into those fields with vLLM's own semantics, so the builder emits each
/// exactly once and a raw override can never silently win again:
/// <list type="bullet">
/// <item>Forms: <c>--flag value</c>, <c>--flag=value</c>, a one-line <c>--flag value</c> entry, underscore spellings
/// (<c>--speculative_config</c>), short <c>-cc</c>, and dotted keys (<c>--speculative-config.num_speculative_tokens=3</c>,
/// <c>--speculative_config.model X</c>, <c>-cc.cudagraph_mode=PIECEWISE</c>, <c>--compilation-config.pass_config.x=true</c>).</item>
/// <item>ExtraArgs come after the typed arguments on the command line, so they win: a scalar flag replaces the typed
/// value (last one wins); a full JSON object replaces the whole speculative/compilation object (vLLM keeps the last
/// <c>--speculative-config</c>). Dotted keys are special, as verified in the image: when any dotted key of a config
/// is present, vLLM builds that config from the dotted keys alone (merged, last wins per key) and discards every full
/// JSON object for it, including the typed one, whatever the order.</item>
/// <item>Every JSON key is kept (lossless); unrelated flags stay in ExtraArgs in their order.</item>
/// </list>
/// Applied when the registry loads (migration, persisted on the next save) and on every save, by the builder before it
/// emits, and by the parser reading a container's command line back. Pure function.
/// <para><b>Tensor parallel size</b> (design §5.1, MN-106): on a placement that selects GPUs
/// (<see cref="ModelPlacement.GpuUuids"/>), <c>--tensor-parallel-size</c> (<c>-tp</c>) is owned by the placement: the
/// builder emits it from the number of placed GPUs, so a raw copy in ExtraArgs is lifted out (the placement wins). On
/// unified and GPU-less placements (every P1 definition) the flag is not owned and stays in ExtraArgs untouched, so their
/// command lines and legacy hashes are exactly as before.</para>
/// </summary>
public static class VllmArgsNormalizer
{
    public const string CudaGraphModeKey = "cudagraph_mode";

    /// <summary>The flag the builder emits for a multi-GPU placement (design §5.1).</summary>
    public const string TensorParallelFlag = "--tensor-parallel-size";

    /// <summary>The editor's name for where the tensor parallel size comes from.</summary>
    public const string TensorParallelField = "GPUs (tensor parallel size = number of placed GPUs)";

    private enum Owned
    {
        Speculative,
        Compilation,
        MaxNumSeqs,
        MaxNumBatchedTokens,
        KvCacheDtype,
        PrefixCaching,
        AsyncScheduling,
        MambaCacheMode,
        TensorParallel,
    }

    private sealed record Op(Owned Kind, string Flag, string? Value, IReadOnlyList<string>? Path, bool? Bool, JsonFields? Json, int? Int);

    private sealed record Classified(Owned Kind, string Flag, IReadOnlyList<string>? Path, string? InlineValue, bool? Bool);

    /// <summary>The editor's name for each owned field.</summary>
    private static string FieldName(Owned kind) => kind switch
    {
        Owned.Speculative => "Speculative decoding",
        Owned.Compilation => "CUDA graph mode / compilation config",
        Owned.MaxNumSeqs => "Max num seqs",
        Owned.MaxNumBatchedTokens => "Max batched tokens",
        Owned.KvCacheDtype => "KV cache dtype",
        Owned.PrefixCaching => "Prefix caching",
        Owned.AsyncScheduling => "Async scheduling",
        Owned.TensorParallel => TensorParallelField,
        _ => "Mamba cache mode",
    };

    public static ModelDefinition Apply(ModelDefinition model) => Normalize(model).Definition;

    /// <summary>
    /// True when <paramref name="token"/> is a flag mirbadllm owns (it would be lifted out of ExtraArgs) on a definition
    /// whose placement selects no GPUs.
    /// </summary>
    public static bool IsOwnedFlag(string token) => Classify(token, tensorParallelOwned: false) is not null;

    /// <summary>True when the tensor parallel size is owned by <paramref name="model"/>'s placement (it selects GPUs).</summary>
    public static bool OwnsTensorParallel(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Placement?.GpuUuids is { Count: > 0 };
    }

    public static NormalizedDefinition Normalize(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var extra = model.ExtraArgs ?? [];
        var (ops, remaining, problems) = Scan(extra, OwnsTensorParallel(model));

        // A CompilationConfig that carries cudagraph_mode (e.g. from the API) is split the same way; the typed
        // field wins when both are set.
        var compilationExtra = model.CompilationConfig;
        bool fixCompilation = compilationExtra is not null &&
            (compilationExtra.IsEmpty || compilationExtra.ContainsKey(CudaGraphModeKey));

        if (ops.Count == 0 && !fixCompilation)
        {
            return new NormalizedDefinition(model, [], problems);
        }

        var result = model;
        if (fixCompilation)
        {
            string? fromExtra = compilationExtra!.GetString(CudaGraphModeKey);
            var rest = fromExtra is null ? compilationExtra : compilationExtra.Without(CudaGraphModeKey);
            result = result with
            {
                CudaGraphMode = result.CudaGraphMode ?? fromExtra,
                CompilationConfig = rest.IsEmpty ? null : rest,
            };
        }

        JsonFields? speculative = result.Speculative?.ToFields();
        bool speculativeTouched = false;
        JsonFields compilation = CompilationFields(result);
        bool compilationTouched = false;

        // vLLM (verified in the image): when any dotted key of a config is present, the config is built from the
        // dotted keys alone and every full JSON object for it (including the typed one) is discarded, in any order.
        bool speculativeDotted = ops.Any(o => o.Kind == Owned.Speculative && o.Path is not null);
        bool compilationDotted = ops.Any(o => o.Kind == Owned.Compilation && o.Path is not null);
        if (speculativeDotted)
        {
            speculative = JsonFields.Empty;
        }

        if (compilationDotted)
        {
            compilation = JsonFields.Empty;
        }
        var lifted = new List<LiftedArg>(ops.Count);

        foreach (var op in ops)
        {
            lifted.Add(new LiftedArg(op.Flag, FieldName(op.Kind)));
            switch (op.Kind)
            {
                case Owned.Speculative:
                    if (op.Path is not null)
                    {
                        speculative = speculative!.SetPath(op.Path, JsonFields.RawFromCliValue(op.Value!));
                    }
                    else if (!speculativeDotted)
                    {
                        speculative = op.Json!;
                    }

                    speculativeTouched = true;
                    break;
                case Owned.Compilation:
                    if (op.Path is not null)
                    {
                        compilation = compilation.SetPath(op.Path, JsonFields.RawFromCliValue(op.Value!));
                    }
                    else if (!compilationDotted)
                    {
                        compilation = op.Json!;
                    }

                    compilationTouched = true;
                    break;
                case Owned.MaxNumSeqs:
                    result = result with { MaxNumSeqs = op.Int!.Value };
                    break;
                case Owned.MaxNumBatchedTokens:
                    result = result with { MaxNumBatchedTokens = op.Int!.Value };
                    break;
                case Owned.KvCacheDtype:
                    result = result with { KvCacheDtype = op.Value! };
                    break;
                case Owned.PrefixCaching:
                    result = result with { EnablePrefixCaching = op.Bool };
                    break;
                case Owned.AsyncScheduling:
                    result = result with { AsyncScheduling = op.Bool };
                    break;
                case Owned.MambaCacheMode:
                    result = result with { MambaCacheMode = op.Value };
                    break;
                case Owned.TensorParallel:
                    // Owned by the placement: the builder emits it from the GPU count, so the raw copy is dropped.
                    break;
            }
        }

        if (speculativeTouched)
        {
            result = result with { Speculative = speculative is null || speculative.IsEmpty ? null : SpeculativeDecoding.FromFields(speculative) };
        }

        if (compilationTouched)
        {
            string? mode = compilation.GetString(CudaGraphModeKey);
            var rest = mode is null ? compilation : compilation.Without(CudaGraphModeKey);
            result = result with { CudaGraphMode = mode, CompilationConfig = rest.IsEmpty ? null : rest };
        }

        if (ops.Count > 0)
        {
            result = result with { ExtraArgs = remaining };
        }

        return new NormalizedDefinition(result, lifted, problems);
    }

    /// <summary>The whole compilation object of a definition: <see cref="ModelDefinition.CompilationConfig"/> plus <c>cudagraph_mode</c>.</summary>
    public static JsonFields CompilationFields(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fields = model.CompilationConfig ?? JsonFields.Empty;
        return string.IsNullOrEmpty(model.CudaGraphMode) ? fields : fields.WithString(CudaGraphModeKey, model.CudaGraphMode);
    }

    private static (List<Op> Ops, List<string> Remaining, List<string> Problems) Scan(IReadOnlyList<string> args, bool tensorParallelOwned)
    {
        var ops = new List<Op>();
        var remaining = new List<string>(args.Count);
        var problems = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            var c = Classify(token, tensorParallelOwned);
            if (c is null)
            {
                remaining.Add(token);
                continue;
            }

            if (c.Kind is Owned.PrefixCaching or Owned.AsyncScheduling)
            {
                bool? value = c.Bool;
                if (c.InlineValue is not null)
                {
                    bool? parsed = ParseBool(c.InlineValue);
                    if (parsed is null)
                    {
                        problems.Add($"{c.Flag}: '{c.InlineValue}' is not true or false.");
                        remaining.Add(token);
                        continue;
                    }

                    value = value == parsed;
                }

                ops.Add(new Op(c.Kind, c.Flag, null, null, value, null, null));
                continue;
            }

            string? raw = c.InlineValue;
            var consumed = new List<string> { token };
            if (raw is null && i + 1 < args.Count)
            {
                raw = args[++i];
                consumed.Add(raw);
            }

            if (raw is null)
            {
                problems.Add($"{c.Flag} needs a value.");
                remaining.AddRange(consumed);
                continue;
            }

            string? problem = null;
            JsonFields? json = null;
            int? number = null;
            switch (c.Kind)
            {
                case Owned.Speculative or Owned.Compilation when c.Path is null:
                    if (!JsonFields.TryParse(raw, out var parsedJson))
                    {
                        problem = $"{c.Flag}: the value is not a JSON object.";
                    }

                    json = parsedJson;
                    break;
                case Owned.MaxNumSeqs or Owned.MaxNumBatchedTokens or Owned.TensorParallel:
                    if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                    {
                        number = n;
                    }
                    else
                    {
                        problem = $"{c.Flag}: '{raw}' is not a positive whole number.";
                    }

                    break;
                default:
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        problem = $"{c.Flag} needs a value.";
                    }

                    raw = raw.Trim();
                    break;
            }

            if (problem is not null)
            {
                problems.Add(problem);
                remaining.AddRange(consumed);
                continue;
            }

            ops.Add(new Op(c.Kind, c.Flag, raw, c.Path, null, json, number));
        }

        return (ops, remaining, problems);
    }

    /// <summary>
    /// Recognizes an owned flag in any of its spellings; null for anything else. The tensor parallel size is owned only
    /// when <paramref name="tensorParallelOwned"/> (the placement selects GPUs).
    /// </summary>
    private static Classified? Classify(string token, bool tensorParallelOwned)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string text = token.Trim();
        if (text.Length < 2 || text[0] != '-')
        {
            return null;
        }

        // The value may be attached with '=' or, in a one-line editor entry, after a space: the first of the two.
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        int space = text.IndexOfAny([' ', '\t']);
        int split = eq < 0 ? space : space < 0 ? eq : Math.Min(eq, space);
        string name = split < 0 ? text : text[..split];
        string? inline = split < 0 ? null : text[(split + 1)..].Trim();

        string body;
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            body = name[2..];
        }
        else if (name == "-cc" || name.StartsWith("-cc.", StringComparison.Ordinal))
        {
            body = "compilation-config" + name[3..];
        }
        else if (name == "-tp")
        {
            body = "tensor-parallel-size";
        }
        else
        {
            return null;
        }

        int dot = body.IndexOf('.', StringComparison.Ordinal);
        string flag = (dot < 0 ? body : body[..dot]).Replace('_', '-');
        IReadOnlyList<string>? path = null;
        if (dot >= 0)
        {
            path = body[(dot + 1)..].Split('.').Select(k => k.Replace('-', '_')).ToList();
            if (path.Any(string.IsNullOrEmpty))
            {
                return null;
            }
        }

        Owned? kind = flag switch
        {
            "speculative-config" => Owned.Speculative,
            "compilation-config" => Owned.Compilation,
            "max-num-seqs" => Owned.MaxNumSeqs,
            "max-num-batched-tokens" => Owned.MaxNumBatchedTokens,
            "kv-cache-dtype" => Owned.KvCacheDtype,
            "mamba-cache-mode" => Owned.MambaCacheMode,
            "enable-prefix-caching" or "no-enable-prefix-caching" => Owned.PrefixCaching,
            "async-scheduling" or "no-async-scheduling" => Owned.AsyncScheduling,
            "tensor-parallel-size" when tensorParallelOwned => Owned.TensorParallel,
            _ => null,
        };

        if (kind is null || (path is not null && kind is not (Owned.Speculative or Owned.Compilation)))
        {
            return null;
        }

        bool? boolean = kind is Owned.PrefixCaching or Owned.AsyncScheduling ? !flag.StartsWith("no-", StringComparison.Ordinal) : null;
        return new Classified(kind.Value, name, path, inline, boolean);
    }

    private static bool? ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" or "YES" or "ON" => true,
        "FALSE" or "0" or "NO" or "OFF" => false,
        _ => null,
    };
}
