using System.ComponentModel;
using System.Text.Json;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>云监控指标查询工具，统一走 metrics.{region} 端点。</summary>
[McpServerToolType]
public sealed class CmsTools(GenericApiCaller caller, ILogger<CmsTools> logger)
{
    /// <summary>读取 ECS 实例的 CPU 使用率。</summary>
    [McpServerTool(Name = "CMS_GetCpuUsageData")]
    [Description("获取ECS实例的CPU使用率数据")]
    public Task<CallToolResult> GetCpuUsageData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "cpu_total", cancellationToken);

    /// <summary>读取 ECS 实例 1 分钟平均负载。</summary>

    [McpServerTool(Name = "CMS_GetCpuLoadavgData")]
    [Description("获取CPU一分钟平均负载指标数据")]
    public Task<CallToolResult> GetCpuLoadavgData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "load_1m", cancellationToken);

    /// <summary>读取 ECS 实例 5 分钟平均负载。</summary>

    [McpServerTool(Name = "CMS_GetCpuloadavg5mData")]
    [Description("获取CPU五分钟平均负载指标数据")]
    public Task<CallToolResult> GetCpuloadavg5mData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "load_5m", cancellationToken);

    /// <summary>读取 ECS 实例 15 分钟平均负载。</summary>

    [McpServerTool(Name = "CMS_GetCpuloadavg15mData")]
    [Description("获取CPU十五分钟平均负载指标数据")]
    public Task<CallToolResult> GetCpuloadavg15mData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "load_15m", cancellationToken);

    /// <summary>读取 ECS 实例已用内存。</summary>

    [McpServerTool(Name = "CMS_GetMemUsedData")]
    [Description("获取ECS实例已使用内存数据")]
    public Task<CallToolResult> GetMemUsedData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "memory_usedspace", cancellationToken);

    /// <summary>读取 ECS 实例内存使用率。</summary>

    [McpServerTool(Name = "CMS_GetMemUsageData")]
    [Description("获取ECS实例内存使用率数据")]
    public Task<CallToolResult> GetMemUsageData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "memory_usedutilization", cancellationToken);

    /// <summary>读取 ECS 实例磁盘使用率。</summary>

    [McpServerTool(Name = "CMS_GetDiskUsageData")]
    [Description("获取ECS实例磁盘使用率数据")]
    public Task<CallToolResult> GetDiskUsageData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "diskusage_utilization", cancellationToken);

    /// <summary>读取 ECS 实例磁盘总量。</summary>

    [McpServerTool(Name = "CMS_GetDiskTotalData")]
    [Description("获取ECS实例磁盘总量数据")]
    public Task<CallToolResult> GetDiskTotalData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "diskusage_total", cancellationToken);

    /// <summary>读取 ECS 实例已用磁盘空间。</summary>

    [McpServerTool(Name = "CMS_GetDiskUsedData")]
    [Description("获取ECS实例磁盘已使用数据")]
    public Task<CallToolResult> GetDiskUsedData(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) => GetMetricAsync(RegionId, InstanceIds, "diskusage_used", cancellationToken);

    /// <summary>查询指定指标最近一次数据点并返回原始 JSON。</summary>
    private async Task<CallToolResult> GetMetricAsync(
        string regionId,
        IReadOnlyList<string> instanceIds,
        string metricName,
        CancellationToken cancellationToken)
    {
        try
        {
            var dimensions = instanceIds.Select(id => new { instanceId = id }).ToArray();
            var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
                ["Namespace"] = JsonSerializer.SerializeToElement("acs_ecs_dashboard"),
                ["MetricName"] = JsonSerializer.SerializeToElement(metricName),
                ["Dimensions"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(dimensions)),
            };

            var result = await caller.CallAsync(
                "cms",
                "DescribeMetricLast",
                arguments,
                $"metrics.{regionId.ToLowerInvariant()}.aliyuncs.com",
                cancellationToken);

            var datapoints = ToolResults.Find(result, "Datapoints");
            return ToolResults.Text(datapoints ?? result);
        }
        catch (Exception exception)
        {
            logger.LogWarning("CMS metric {Metric} failed: {Message}", metricName, exception.Message);
            return ToolResults.Error(exception);
        }
    }
}
