using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

[McpServerToolType]
/// <summary>通用工具：场景说明、API 发现与泛化调用。</summary>
public sealed partial class CommonTools(ServerOptions options, ApiMetaClient metaClient, GenericApiCaller caller)
{
    private static readonly Dictionary<string, string> SupportedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ecs"] = "Elastic Compute Service (ECS)",
        ["oos"] = "Operations Orchestration Service (OOS)",
        ["rds"] = "Relational Database Service (RDS)",
        ["vpc"] = "Virtual Private Cloud (VPC)",
        ["slb"] = "Server Load Balancer (SLB)",
        ["ess"] = "Elastic Scaling (ESS)",
        ["ros"] = "Resource Orchestration Service (ROS)",
        ["cbn"] = "Cloud Enterprise Network (CBN)",
        ["dds"] = "MongoDB Database Service (DDS)",
        ["r-kvstore"] = "Cloud database Tair (compatible with Redis) (R-KVStore)",
        ["bssopenapi"] = "Billing and Cost Management (BssOpenAPI)",
    };

    /// <summary>返回运维与部署场景的使用说明。</summary>

    [McpServerTool(Name = "PromptUnderstanding")]
    [Description("Always use this tool first to understand the user's query and convert it into suggestions from Alibaba Cloud experts.")]
    public Task<CallToolResult> PromptUnderstanding(CancellationToken cancellationToken = default)
    {
        var content = LoadPrompt();
        if (options.ServiceCodes.Count > 0)
        {
            var replacement = "Supported Services:\n   - " + string.Join(
                "\n   - ",
                options.ServiceCodes.Select(code => $"{code}: {(SupportedServices.TryGetValue(code, out var name) ? name : code)}"));
            content = SupportedServicesRegex().Replace(content, replacement);
        }

        return Task.FromResult(ToolResults.Text(content));
    }

    /// <summary>列出指定服务的 API 名称。</summary>

    [McpServerTool(Name = "ListAPIs")]
    [Description("Use PromptUnderstanding tool first to understand the user's query, Get the corresponding API list information through the service name to prepare for the subsequent selection of the appropriate API to call")]
    public async Task<CallToolResult> ListApis(
        [Description("AlibabaCloud service code")] string service,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var apis = await metaClient.GetApiNamesAsync(service, cancellationToken);
            return ToolResults.Text(new JsonArray(apis.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()));
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>读取单个 API 的参数元数据。</summary>

    [McpServerTool(Name = "GetAPIInfo")]
    [Description("Use PromptUnderstanding tool first to understand the user's query, After specifying the service name and API name, get the detailed API META of the corresponding API")]
    public async Task<CallToolResult> GetApiInfo(
        [Description("AlibabaCloud service code")] string service,
        [Description("AlibabaCloud api name")] string api,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var meta = await metaClient.GetApiMetaAsync(service, api, cancellationToken);
            return ToolResults.Text(JsonSerializer.SerializeToNode(meta));
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>以泛化方式调用任意阿里云 API。</summary>

    [McpServerTool(Name = "CommonAPICaller")]
    [Description("Use PromptUnderstanding tool first to understand the user's query, Perform the actual call by specifying the Service, API, and Parameters")]
    public async Task<CallToolResult> CommonApiCaller(
        [Description("AlibabaCloud service code")] string service,
        [Description("AlibabaCloud api name")] string api,
        [Description("AlibabaCloud ECS instance ID List")] Dictionary<string, JsonElement>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var arguments = parameters ?? [];
            return ToolResults.Text(await caller.CallAsync(service, api, arguments, cancellationToken));
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>读取嵌入的 PromptUnderstanding 并替换支持服务清单。</summary>
    private static string LoadPrompt()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("PROMPT_UNDERSTANDING.md", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return "Supported Services:\n   - ecs: Elastic Compute Service (ECS)";
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"Supported Services\s*:\s*\n(?:\s{3}- .+?\n)+")]
    /// <summary>匹配文本中的支持服务清单占位。</summary>
    private static partial Regex SupportedServicesRegex();
}

[McpServerToolType]
/// <summary>普通模式下注册的 ECS 实例查询工具。</summary>
public sealed class InstanceTools(GenericApiCaller caller)
{
    /// <summary>查询 ECS 实例列表。</summary>
    [McpServerTool(Name = "ECS_DescribeInstances")]
    [Description("查询指定ECS实例的详细信息。此工具要求必须提供实例ID列表，避免随意查询所有实例。注意：此工具仅用于查询用户明确指定的实例信息，不允许用于扫描或枚举所有实例。")]
    public async Task<CallToolResult> DescribeInstances(
        [Description("AlibabaCloud ECS instance ID List (required)")] List<string> instance_ids,
        [Description("AlibabaCloud region ID")] string region_id = "cn-hangzhou",
        CancellationToken cancellationToken = default)
    {
        if (instance_ids is null || instance_ids.Count == 0)
        {
            return ToolResults.Error("InvalidParameter: instance_ids is required and cannot be empty");
        }

        try
        {
            var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(region_id),
                ["InstanceIds"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(instance_ids)),
            };
            return ToolResults.Text(await caller.CallAsync("ecs", "DescribeInstances", arguments, cancellationToken));
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }
}
