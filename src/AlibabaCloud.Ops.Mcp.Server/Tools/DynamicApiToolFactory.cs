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

    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.Ordinal)
    {
        ["string"] = "string",
        ["integer"] = "integer",
        ["boolean"] = "boolean",
        ["array"] = "array",
        ["object"] = "object",
        ["number"] = "number",
    };

    /// <summary>创建单个动态工具，元数据缺失时返回 null 而不中断启动。</summary>
    public async Task<McpServerTool?> CreateAsync(string service, string api, CancellationToken cancellationToken)
    {
        try
        {
            var standardService = await metaClient.GetStandardServiceCodeAsync(service, cancellationToken);
            var standardApi = await metaClient.GetStandardApiAsync(standardService, api, cancellationToken);
            var meta = await metaClient.GetApiMetaAsync(standardService, standardApi, cancellationToken);
            var toolName = $"{standardService.ToUpperInvariant()}_{standardApi}";
            var schema = BuildInputSchema(standardService, meta);
            var regionDefault = RegionDefault(schema);

            Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> handler = async (context, token) =>
            {
                try
                {
                    var arguments = context.Params?.Arguments is { } values
                        ? new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                    if (regionDefault is not null && !arguments.ContainsKey("RegionId"))
                    {
                        arguments["RegionId"] = JsonSerializer.SerializeToElement(regionDefault);
                    }

                    RequireArguments(schema, arguments);
                    var result = await caller.CallAsync(standardService, standardApi, arguments, token);
                    return ToolResults.Text(result);
                }
                catch (Exception exception)
                {
                    return ToolResults.Error(exception);
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

            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
            logger.LogInformation("Registered dynamic tool {Tool}", toolName);
            return tool;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Skipped dynamic tool {Service}.{Api}: {Message}", service, api, exception.Message);
            return null;
        }
    }

    /// <summary>由元数据生成 JSON Schema：必填项排前，补 RegionId 默认值与 ECS 列表参数。</summary>
    internal static JsonObject BuildInputSchema(string service, ApiMeta meta)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var parameters = meta.Parameters ?? [];

        foreach (var requiredPass in new[] { true, false })
        {
            foreach (var parameter in parameters)
            {
                var name = parameter.Name;
                if (string.IsNullOrWhiteSpace(name) || name.Contains('.'))
                {
                    continue;
                }

                var schema = parameter.Schema;
                var isRequired = schema.TryGetProperty("required", out var requiredElement)
                                 && requiredElement.ValueKind == JsonValueKind.True;
                if (isRequired != requiredPass)
                {
                    continue;
                }

                var type = schema.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;
                var description = schema.TryGetProperty("description", out var descriptionElement)
                    ? descriptionElement.GetString() ?? string.Empty
                    : string.Empty;
                var example = schema.TryGetProperty("example", out var exampleElement)
                    ? exampleElement.ToString()
                    : string.Empty;

                var ecsListParameter = string.Equals(service, "ecs", StringComparison.OrdinalIgnoreCase)
                                       && type == "string"
                                       && EcsListParameters.Contains(name);
                var schemaType = ecsListParameter ? "array" : TypeMap.GetValueOrDefault(type, "string");

                var property = new JsonObject();
                if (schemaType == "object")
                {
                    property["additionalProperties"] = true;
                }

                if (!isRequired)
                {
                    property["default"] = null;
                }

                property["description"] = $"{description} 参数类型: {type},参数示例：{example}";
                if (schemaType == "array")
                {
                    property["items"] = new JsonObject();
                }

                property["title"] = SchemaTitle(name);
                property["type"] = schemaType;
                properties[name] = property;

                if (isRequired)
                {
                    required.Add(name);
                }
            }
        }

        if (!properties.ContainsKey("RegionId"))
        {
            properties["RegionId"] = new JsonObject
            {
                ["default"] = "cn-hangzhou",
                ["description"] = "地域ID",
                ["title"] = SchemaTitle("RegionId"),
                ["type"] = "string",
            };
        }

        var result = new JsonObject { ["properties"] = properties };
        if (required.Count > 0)
        {
            result["required"] = required;
        }

        result["type"] = "object";
        return result;
    }

    /// <summary>校验必填参数，缺失时抛 InvalidParameter。</summary>
    internal static void RequireArguments(JsonObject schema, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (schema["required"] is not JsonArray required)
        {
            return;
        }

        foreach (var name in required.OfType<JsonValue>().Select(x => x.GetValue<string>()))
        {
            if (!arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                throw new OpsException("InvalidParameter", $"{name} is required and cannot be empty.");
            }
        }
    }

    /// <summary>按 pydantic 规则生成 title：下划线转空格，词首大写其余小写。</summary>
    internal static string SchemaTitle(string name)
    {
        var spaced = name.Replace('_', ' ');
        var builder = new System.Text.StringBuilder(spaced.Length);
        var previousLetter = false;
        foreach (var character in spaced)
        {
            if (char.IsLetter(character))
            {
                builder.Append(previousLetter ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character));
                previousLetter = true;
            }
            else
            {
                builder.Append(character);
                previousLetter = false;
            }
        }

        return builder.ToString();
    }

    /// <summary>仅当 RegionId 是自动补充的 cn-hangzhou 默认值时才注入请求。</summary>
    private static string? RegionDefault(JsonObject schema)
    {
        var value = schema["properties"]?["RegionId"]?["default"]?.GetValue<string>();
        return value == "cn-hangzhou" ? value : null;
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
}
