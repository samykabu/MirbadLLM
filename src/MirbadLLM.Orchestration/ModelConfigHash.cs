using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// A stable fingerprint of everything a model container is launched with: the image plus the exact
/// <c>vllm serve</c> arguments from <see cref="VllmArgsBuilder"/> (which already include the budget-derived
/// <c>--gpu-memory-utilization</c>). Settings that are not launch arguments (<see cref="ModelDefinition.AutoStart"/>)
/// do not change it. The hash is taken over the canonical argument list (<see cref="VllmArgsParser.Canonicalize"/>),
/// so it never depends on how the definition was serialized or spelled: a container launched before the canonical
/// configuration (an owned flag repeated in its raw ExtraArgs) hashes the same as the normalized definition vLLM
/// effectively ran, and a builder output hashes exactly as it always did.
/// </summary>
public static class ModelConfigHash
{
    public static string Compute(ModelDefinition model, string defaultImage, double totalGiB)
    {
        string image = string.IsNullOrEmpty(model.Image) ? defaultImage : model.Image;
        return Compute(image, VllmArgsBuilder.Build(model, totalGiB));
    }

    /// <summary>The hash of a launch: <paramref name="args"/> are canonicalized first.</summary>
    public static string Compute(string image, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var text = new StringBuilder(image);
        foreach (string arg in VllmArgsParser.Canonicalize(args))
        {
            // NUL cannot appear in a command-line argument, so it separates fields unambiguously.
            text.Append('\0').Append(arg);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
