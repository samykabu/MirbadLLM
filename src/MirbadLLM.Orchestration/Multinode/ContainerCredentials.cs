using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>The secret values of one container start: read from the saved tokens just before they are sent, and kept nowhere else.</summary>
public sealed record ResolvedContainerCredentials(LaunchEnvironment Environment, RegistryCredentials? Registry)
{
    public static ResolvedContainerCredentials None { get; } = new(LaunchEnvironment.None, null);
}

/// <summary>Turns the saved-token names on a SGLang or NIM model into values.</summary>
public static class ContainerCredentials
{
    /// <exception cref="ModelStartConflictException">A named token isn't saved, so the start can't go ahead.</exception>
    public static async Task<ResolvedContainerCredentials> ResolveAsync(ITokenStore? tokens, ModelDefinition model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Runtime is not (RuntimeKind.Sglang or RuntimeKind.Nim) || model.Container is not { } settings ||
            (settings.SecretEnv is not { Count: > 0 } && settings.RegistryLogin is null))
        {
            return ResolvedContainerCredentials.None;
        }

        var saved = tokens is null ? [] : await tokens.ListAsync(ct).ConfigureAwait(false);
        async Task<string> ValueOf(string tokenName, string usedFor)
        {
            var token = saved.FirstOrDefault(t => string.Equals(t.Name, tokenName, StringComparison.OrdinalIgnoreCase));
            string? value = token is null ? null : await tokens!.GetValueAsync(token.Id, ct).ConfigureAwait(false);
            return value ?? throw new ModelStartConflictException(
                $"Model '{model.Id}' needs the saved token '{tokenName}' for {usedFor}, but it isn't under Access > Tokens.");
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (variable, tokenName) in settings.SecretEnv ?? new Dictionary<string, string>())
        {
            variables[variable] = await ValueOf(tokenName, variable).ConfigureAwait(false);
        }

        RegistryCredentials? registry = settings.RegistryLogin is { } login
            ? new RegistryCredentials { Username = login.Username, Password = await ValueOf(login.TokenName, "its registry login").ConfigureAwait(false) }
            : null;
        return new ResolvedContainerCredentials(new LaunchEnvironment(variables, [.. variables.Keys]), registry);
    }
}
