using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Mcp;

// 无状态 HTTP 每次请求重建工具集合，动态工具与白名单必须在每次配置时重新应用。
/// <summary>在每次 McpServerOptions 生成后回填动态工具并应用白名单。</summary>
public sealed class ToolOptionsSetup(
    ServerOptions options,
    DynamicToolRegistry registry) : IPostConfigureOptions<McpServerOptions>
{
    /// <summary>按当前参数补齐动态工具、过滤非白名单工具。</summary>
    public void PostConfigure(string? name, McpServerOptions mcpOptions)
    {
        var collection = mcpOptions.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        registry.ApplyTo(collection);

        if (options.CodeDeploy || options.VisibleToolNames.Count == 0)
        {
            return;
        }

        var allowed = new HashSet<string>(options.VisibleToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var tool in collection.ToList())
        {
            if (!allowed.Contains(tool.ProtocolTool.Name))
            {
                collection.Remove(tool);
            }
        }
    }
}
