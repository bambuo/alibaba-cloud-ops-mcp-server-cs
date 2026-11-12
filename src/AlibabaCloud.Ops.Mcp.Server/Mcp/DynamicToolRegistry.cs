using AlibabaCloud.Ops.Mcp.Server.Options;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Mcp;

/// <summary>按启动参数规划并创建运行时动态工具，默认 11 个、白名单或 `--extra-config` 可增减。</summary>
public sealed class DynamicToolRegistry(
    ServerOptions options,
    DynamicApiToolFactory factory,
    ILogger<DynamicToolRegistry> logger)
{
    private readonly List<McpServerTool> _tools = [];
    private static readonly (string Service, string Api)[] DefaultApis =
    [
        ("ecs", "DescribeInstances"),
        ("ecs", "DescribeRegions"),
        ("ecs", "DescribeZones"),
        ("ecs", "DescribeAccountAttributes"),
        ("ecs", "DescribeAvailableResource"),
        ("ecs", "DescribeImages"),
        ("ecs", "DescribeSecurityGroups"),
        ("ecs", "DeleteInstances"),
        ("vpc", "DescribeVpcs"),
        ("vpc", "DescribeVSwitches"),
        ("rds", "DescribeDBInstances"),
    ];

    // HTTP 无状态传输会按请求重新生成 McpServerOptions，注册结果通过 PostConfigure 每次回填。
    /// <summary>拉取元数据并创建动态工具，单个 API 失败只跳过该项。</summary>
    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        foreach (var (service, api) in BuildPlan())
        {
            var tool = await factory.CreateAsync(service, api, cancellationToken);
            if (tool is not null)
            {
                _tools.Add(tool);
            }
        }

        logger.LogInformation("Dynamic tools registered: {Count}", _tools.Count);
    }

    /// <summary>把已创建的工具补进指定集合，按名称去重。</summary>
    public void ApplyTo(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        foreach (var tool in _tools)
        {
            if (!collection.Any(existing => existing.ProtocolTool.Name == tool.ProtocolTool.Name))
            {
                collection.TryAdd(tool);
            }
        }
    }

    /// <summary>计算待注册的「服务码/API」清单：部署模式为空，白名单优先于默认清单。</summary>
    internal IReadOnlyList<(string Service, string Api)> BuildPlan()
    {
        if (options.CodeDeploy)
        {
            return [];
        }

        var plan = new List<(string Service, string Api)>();
        if (options.VisibleToolNames.Count > 0)
        {
            foreach (var name in options.VisibleToolNames)
            {
                var separator = name.IndexOf('_');
                if (separator <= 0 || separator == name.Length - 1)
                {
                    continue;
                }

                plan.Add((name[..separator].ToLowerInvariant(), name[(separator + 1)..]));
            }

            return plan;
        }

        plan.AddRange(DefaultApis);

        if (options.ExtraConfig is not null)
        {
            foreach (var (service, apis) in ExtraConfigParser.Parse(options.ExtraConfig))
            {
                foreach (var api in apis)
                {
                    if (!plan.Any(x => string.Equals(x.Service, service, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(x.Api, api, StringComparison.OrdinalIgnoreCase)))
                    {
                        plan.Add((service.ToLowerInvariant(), api));
                    }
                }
            }
        }

        return plan;
    }
}
