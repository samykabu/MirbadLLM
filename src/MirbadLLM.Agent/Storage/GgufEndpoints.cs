using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Gguf;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// <see cref="AgentRoutes.GgufInspect"/> (<c>GET /model-folders/{folder}/gguf?file=</c>, design-p3p4 §A.1.10; MN-209).
/// The route is gated by <see cref="AgentFeatures.GgufInspect"/> (reported by <see cref="GgufCapabilityContributor"/>), so
/// an agent without the feature answers 501 <c>not_supported</c> in the request pipeline before this handler runs.
/// <para>Confinement (§A.2.3, §6.4): <c>folder</c> goes through <see cref="ModelRootPaths.Locate"/> (one plain segment,
/// links resolved and kept inside the models root); <c>file</c> is one plain file name ending in <c>.gguf</c> (no
/// directory part, <c>..</c>, drive or stream colon, control character, trailing dot or space, or reserved device name
/// such as <c>NUL.gguf</c>) → else 400 <c>invalid_request</c>; a file link must resolve inside the root, and the reader
/// gets the resolved path, so split parts are looked up next to the real file (and must be plain files there). A split
/// part other than the first is 400. Without <c>file</c> the folder's only model GGUF is read (<c>mmproj*</c> files and
/// parts 2..N excluded): none is 404, several is 400 listing them. Missing folder or file is 404 <c>not_found</c>; a
/// malformed GGUF is 422 <c>gguf_invalid</c>.</para>
/// </summary>
public static class GgufEndpoints
{
    /// <summary>The query parameter naming the file (<see cref="GgufInspectQuery.File"/>).</summary>
    public const string FileQuery = "file";

    private const string GgufExtension = ".gguf";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>Maps the GGUF route at <see cref="Protocol.BasePath"/> + its path, with the route as endpoint metadata.</summary>
    public static IEndpointRouteBuilder MapGgufEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapAgentRoute(AgentRoutes.GgufInspect, (Func<HttpContext, string, Task<IResult>>)InspectAsync);
        return endpoints;
    }

    private static async Task<IResult> InspectAsync(HttpContext http, string folder)
    {
        IGgufReader? reader = http.RequestServices.GetService<IGgufReader>();
        ModelRootPaths? paths = http.RequestServices.GetService<ModelRootPaths>();
        if (reader is null || paths is null)
        {
            return AgentErrorResults.Error(ErrorMappings.AgentStatus(ErrorCodes.NotSupported), ErrorCodes.NotSupported,
                $"GGUF inspection is not configured on this agent ({AgentFeatures.GgufInspect}).");
        }

        string? file = http.Request.Query.TryGetValue(FileQuery, out var values) ? values.ToString() : null;
        try
        {
            var summary = await InspectAsync(reader, paths, folder, string.IsNullOrEmpty(file) ? null : file, http.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(summary, AgentJson.Options, AgentJson.JsonContentType, StatusCodes.Status200OK);
        }
        catch (AgentException ex)
        {
            return AgentErrorResults.Error(ex.StatusCode, ex.Error);
        }
    }

    /// <summary>
    /// Confines <paramref name="folder"/>/<paramref name="file"/> and reads the GGUF (the handler's logic, callable without
    /// HTTP). Throws <see cref="AgentException"/> with the route's error codes.
    /// </summary>
    public static async Task<GgufSummary> InspectAsync(IGgufReader reader, ModelRootPaths paths, string folder, string? file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(paths);
        FolderLocation location = paths.Locate(folder);
        if (location.IsLink && location.ResolvedPath is null)
        {
            throw StorageErrors.InvalidRequest($"'{folder}' is a link that does not resolve inside the models root.");
        }

        if (location.ResolvedPath is not { } directory)
        {
            throw StorageErrors.NotFound($"No folder '{folder}' on this node.");
        }

        string name = file ?? SingleModelFile(folder, directory);
        if (FileNameError(name) is { } error)
        {
            throw StorageErrors.InvalidRequest(error);
        }

        if (GgufReader.ParseSplitName(name) is { Number: not 1 } split)
        {
            throw StorageErrors.InvalidRequest(string.Create(CultureInfo.InvariantCulture,
                $"'{name}' is split part {split.Number} of {split.Count}; inspect the first part."));
        }

        if (ThroughALinkedFolder(paths.Root, Path.Combine(directory, name)))
        {
            throw StorageErrors.InvalidRequest($"'{folder}' is a link that does not resolve inside the models root.");
        }

        string path = ResolveFile(paths.Root, directory, folder, name);
        try
        {
            return await reader.ReadAsync(path, folder, name, ct).ConfigureAwait(false);
        }
        catch (GgufFormatException ex)
        {
            throw StorageErrors.Create(ErrorCodes.GgufInvalid, $"'{folder}/{name}' is not a valid GGUF: {ex.Message}");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw StorageErrors.NotFound($"No file '{name}' in '{folder}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StorageErrors.InvalidRequest($"'{folder}/{name}' cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Why <paramref name="file"/> is not an acceptable GGUF file name, or null: the folder-name rules of
    /// <see cref="ModelRootPaths.NameError"/> (one segment, no separators, <c>:</c>, control characters, leading dot,
    /// trailing dot or space), a <c>.gguf</c> extension, and no reserved device name.
    /// </summary>
    public static string? FileNameError(string? file)
    {
        if (ModelRootPaths.NameError(file) is { } error)
        {
            return error.Replace("folder name", "file name", StringComparison.Ordinal);
        }

        if (!file!.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase) || file.Length == GgufExtension.Length)
        {
            return $"'{file}' is not a .gguf file name.";
        }

        string stem = file[..file.IndexOf('.', StringComparison.Ordinal)].TrimEnd(' ');
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return $"'{file}' is a reserved device name.";
        }

        return null;
    }

    private static string SingleModelFile(string folder, string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var candidates = Directory.EnumerateFiles(directory, "*" + GgufExtension, options)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => n.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            .Where(n => GgufReader.ParseSplitName(n) is null or { Number: 1 })
            .Order(StringComparer.Ordinal)
            .ToList();
        return candidates.Count switch
        {
            0 => throw StorageErrors.NotFound($"'{folder}' has no model GGUF."),
            1 => candidates[0],
            // The names also travel as GgufChoiceDetails, so the editor's picker does not parse this sentence (MN-230).
            _ => throw StorageErrors.Create(
                ErrorCodes.InvalidRequest,
                $"'{folder}' has several model GGUFs; choose one with ?file=: {string.Join(", ", candidates)}.",
                new GgufChoiceDetails(candidates)),
        };
    }

    /// <summary>
    /// True when a folder between <paramref name="root"/> and <paramref name="path"/> (both full, the path inside the root)
    /// is a link (MN-219): a link's final target is only checked as text, and on Linux a target such as
    /// <c>root/hop/model.gguf</c> with <c>root/hop</c> linked elsewhere reads as inside the root while the file is not.
    /// </summary>
    private static bool ThroughALinkedFolder(string root, string path)
    {
        string top = Path.TrimEndingDirectorySeparator(root);
        for (string? parent = Path.GetDirectoryName(path); parent is not null && parent.Length > top.Length; parent = Path.GetDirectoryName(parent))
        {
            if (ModelRootPaths.IsLink(new DirectoryInfo(parent)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveFile(string root, string directory, string folder, string name)
    {
        string path = Path.GetFullPath(Path.Combine(directory, name));
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(directory), PathComparison))
        {
            throw StorageErrors.InvalidRequest($"'{name}' does not resolve to a file directly in '{folder}'.");
        }

        var info = new FileInfo(path);
        if (!ModelRootPaths.IsLink(info))
        {
            return info.Exists ? path : throw StorageErrors.NotFound($"No file '{name}' in '{folder}'.");
        }

        FileSystemInfo? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException)
        {
            target = null;
        }

        string? full = target is null ? null : Path.GetFullPath(target.FullName);
        if (full is null || !ModelRootPaths.IsInside(root, full) || ThroughALinkedFolder(root, full))
        {
            throw StorageErrors.InvalidRequest($"'{folder}/{name}' is a link that does not resolve inside the models root.");
        }

        return File.Exists(full) ? full : throw StorageErrors.NotFound($"'{folder}/{name}' is a link to a missing file.");
    }
}
