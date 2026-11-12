using System.Reflection;
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

    // 白名单模式同样会注册这些静态工具类（含仅白名单模式注册的 InstanceTools），
    // 命中时不再生成同名动态工具，与上游 visible-tools 的注册顺序一致。
    internal static readonly IReadOnlySet<string> StaticToolNames = typeof(DynamicToolRegistry).Assembly
        .GetTypes()
        .Where(type => type.GetCustomAttributes(typeof(McpServerToolTypeAttribute), inherit: false).Any())
        .SelectMany(type => type.GetMethods())
        .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .Where(name => name is not null)
        .Select(name => name!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>把已创建的工具补进指定集合，按名称忽略大小写去重。</summary>
    public void ApplyTo(McpServerPrimitiveCollection<McpServerTool> collection)
    {
        foreach (var tool in _tools)
        {
            if (!collection.Any(existing => string.Equals(
                    existing.ProtocolTool.Name, tool.ProtocolTool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                collection.TryAdd(tool);
            }
        }
    }

    /// <summary>计算待注册的「服务码/API」清单：部署模式为空，默认清单 + extra-config + 白名单派生项。</summary>
    internal IReadOnlyList<(string Service, string Api)> BuildPlan()
    {
        if (options.CodeDeploy)
        {
            return [];
        }

        var plan = new List<(string Service, string Api)>(DefaultApis);

        if (options.ExtraConfig is not null)
        {
            foreach (var (service, apis) in ExtraConfigParser.Parse(options.ExtraConfig))
            {
                foreach (var api in apis)
                {
                    AddIfAbsent(plan, service.ToLowerInvariant(), api);
                }
            }
        }

        foreach (var name in options.VisibleToolNames)
        {
            if (StaticToolNames.Contains(name))
            {
                continue;
            }

            var separator = name.IndexOf('_');
            if (separator <= 0 || separator == name.Length - 1)
            {
                continue;
            }

            AddIfAbsent(plan, name[..separator].ToLowerInvariant(), name[(separator + 1)..]);
        }

        return plan;
    }

    /// <summary>服务码与 API 名均忽略大小写去重后追加。</summary>
    private static void AddIfAbsent(List<(string Service, string Api)> plan, string service, string api)
    {
        if (!plan.Any(x => string.Equals(x.Service, service, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(x.Api, api, StringComparison.OrdinalIgnoreCase)))
        {
            plan.Add((service, api));
        }
    }
}
