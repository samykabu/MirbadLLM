using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace MirbadLLM.Web.Mcp;

/// <summary>One MCP tool as Access &gt; MCP documents it.</summary>
public sealed record McpToolInfo(string Name, string Description, bool ReadOnly, bool Destructive, IReadOnlyList<string> Arguments);

/// <summary>Read from the attributes the server publishes, so the page cannot drift from <c>tools/list</c>.</summary>
public static class McpToolCatalog
{
    public static IReadOnlyList<McpToolInfo> All { get; } = typeof(MirbadMcpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.IsDefined(typeof(McpServerToolAttribute)))
        .OrderBy(m => m.MetadataToken)
        .Select(m =>
        {
            var tool = m.GetCustomAttribute<McpServerToolAttribute>()!;
            return new McpToolInfo(
                tool.Name!,
                m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                tool.ReadOnly,
                tool.Destructive,
                m.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .Select(p => p.HasDefaultValue ? p.Name + "?" : p.Name!)
                    .ToList());
        })
        .ToList();
}
