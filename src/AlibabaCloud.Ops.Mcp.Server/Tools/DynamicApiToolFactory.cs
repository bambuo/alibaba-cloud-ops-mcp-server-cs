using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>按 OpenAPI 元数据动态创建 MCP 工具并生成 inputSchema。</summary>
public sealed class DynamicApiToolFactory(
    ApiMetaClient metaClient,
    GenericApiCaller caller,
    ILogger<DynamicApiToolFactory> logger)
{
    private static readonly HashSet<string> EcsListParameters =
    [
        "HpcClusterIds", "DedicatedHostClusterIds", "DedicatedHostIds",
        "InstanceIds", "DeploymentSetIds", "KeyPairNames", "SecurityGroupIds",
        "diskIds", "repeatWeekdays", "timePoints", "DiskIds", "SnapshotLinkIds",
        "EipAddresses", "PublicIpAddresses", "PrivateIpAddresses",
    ];

    /// <summary>创建单个动态工具，元数据缺失时返回 null 而不中断启动。</summary>
    public async Task<McpServerTool?> CreateAsync(string service, string api, CancellationToken cancellationToken)
    {
        try
        {
            var standardService = await metaClient.GetStandardServiceCodeAsync(service, cancellationToken);
            var apiNames = await metaClient.GetApiNamesAsync(standardService, cancellationToken);
            var standardApi = apiNames.FirstOrDefault(x => string.Equals(x, api, StringComparison.OrdinalIgnoreCase)) ?? api;
            var meta = await metaClient.GetApiMetaAsync(standardService, standardApi, cancellationToken);
            var toolName = $"{standardService.ToUpperInvariant()}_{standardApi}";

            Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> handler = async (context, token) =>
            {
                try
                {
                    var arguments = context.Params?.Arguments is { } values
                        ? new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                    var result = await caller.CallAsync(standardService, standardApi, arguments, token);
                    return Text(result?.ToJsonString() ?? "null");
                }
                catch (Exception exception)
                {
                    return Error(exception.Message);
                }
            };

            var tool = McpServerTool.Create(handler, new McpServerToolCreateOptions
            {
                Name = toolName,
                Description = meta.Summary ?? string.Empty,
                ReadOnly = IsReadOnly(standardApi),
                Destructive = IsDestructive(standardApi),
                Idempotent = IsReadOnly(standardApi),
            });

            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(BuildInputSchema(standardService, meta));
            logger.LogInformation("Registered dynamic tool {Tool}", toolName);
            return tool;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Skipped dynamic tool {Service}.{Api}: {Message}", service, api, exception.Message);
            return null;
        }
    }

    /// <summary>由元数据生成 JSON Schema，补充 RegionId 默认值与 ECS 列表参数。</summary>
    internal static JsonObject BuildInputSchema(string service, ApiMeta meta)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in meta.Parameters ?? [])
        {
            var name = parameter.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('.'))
            {
                continue;
            }

            var schema = parameter.Schema;
            var type = schema.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "string"
                : "string";

            if (string.Equals(service, "ecs", StringComparison.OrdinalIgnoreCase)
                && EcsListParameters.Contains(name)
                && type == "string")
            {
                type = "array";
            }

            var description = schema.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            var example = schema.TryGetProperty("example", out var exampleElement)
                ? exampleElement.ToString()
                : string.Empty;

            var isRequired = schema.TryGetProperty("required", out var requiredElement)
                             && requiredElement.ValueKind == JsonValueKind.True;

            var property = new JsonObject
            {
                ["type"] = type,
                ["description"] = $"{description} 参数类型: {type},参数示例：{example}",
            };

            if (isRequired)
            {
                required.Add(name);
            }
            else
            {
                property["default"] = null;
            }

            properties[name] = property;
        }

        if (!properties.ContainsKey("RegionId"))
        {
            properties["RegionId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "地域ID",
                ["default"] = "cn-hangzhou",
            };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>按 API 名推断只读属性。</summary>
    private static bool IsReadOnly(string api) =>
        api.StartsWith("Describe", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("List", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Get", StringComparison.OrdinalIgnoreCase);

    /// <summary>按 API 名推断破坏性属性。</summary>
    private static bool IsDestructive(string api) =>
        api.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Reboot", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Reset", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Replace", StringComparison.OrdinalIgnoreCase)
        || api.StartsWith("Run", StringComparison.OrdinalIgnoreCase);

    /// <summary>构造成功结果。</summary>
    private static CallToolResult Text(string value) => new()
    {
        Content = [new TextContentBlock { Text = value }],
        IsError = false,
    };

    /// <summary>构造错误结果。</summary>
    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };
}
