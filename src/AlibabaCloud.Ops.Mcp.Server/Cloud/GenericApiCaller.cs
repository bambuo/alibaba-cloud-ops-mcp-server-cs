using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>以 OpenAPI 泛化调用执行任意阿里云 RPC/ROA 接口。</summary>
public sealed class GenericApiCaller(
    ApiMetaClient metaClient,
    EndpointResolver endpointResolver,
    ICredentialProvider credentialProvider,
    ILogger<GenericApiCaller> logger)
{
    private static readonly HashSet<string> EcsListParameters =
    [
        "HpcClusterIds", "DedicatedHostClusterIds", "DedicatedHostIds",
        "InstanceIds", "DeploymentSetIds", "KeyPairNames", "SecurityGroupIds",
        "diskIds", "repeatWeekdays", "timePoints", "DiskIds", "SnapshotLinkIds",
        "EipAddresses", "PublicIpAddresses", "PrivateIpAddresses",
    ];

    /// <summary>按服务与地域自动选择端点后发起调用。</summary>
    public Task<JsonNode?> CallAsync(
        string service,
        string api,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken = default) =>
        CallAsync(service, api, parameters, null, cancellationToken);

    /// <summary>发起调用；endpointOverride 用于 CMS 等固定 metrics 端点的服务。</summary>
    public async Task<JsonNode?> CallAsync(
        string service,
        string api,
        IReadOnlyDictionary<string, JsonElement> parameters,
        string? endpointOverride,
        CancellationToken cancellationToken = default)
    {
        var meta = await metaClient.GetApiMetaAsync(service, api, cancellationToken);
        var style = await metaClient.GetServiceStyleAsync(service, cancellationToken);
        var regionId = ReadRegion(parameters);

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            query[key] = SerializeQueryValue(service, key, value);
        }

        var credentials = credentialProvider.Resolve();
        var config = BuildConfig(credentials, endpointOverride ?? endpointResolver.Resolve(service, regionId));
        var request = BuildParams(meta, style, api);

        var openApiRequest = new OpenApiRequest { Query = query };
        logger.LogInformation("Calling {Service}.{Api} in {Region}", service, api, regionId);

        var client = new AlibabaCloud.OpenApiClient.Client(config);
        var response = await client.CallApiAsync(request, openApiRequest, new Darabonba.Models.RuntimeOptions());

        if (response.TryGetValue("statusCode", out var status) && status is int code && code >= 400)
        {
            var message = response.TryGetValue("body", out var body) ? body?.ToString() : null;
            throw new CloudApiException($"Http{code}", message ?? "Request failed");
        }

        return ToNode(response);
    }

    /// <summary>组装调用所需的 Action、Version、路径与 HTTP 方法。</summary>
    internal static Params BuildParams(ApiMeta meta, string style, string api) => new()
    {
        Action = api,
        Version = meta.Version,
        Protocol = "HTTPS",
        Pathname = string.IsNullOrWhiteSpace(meta.Path) ? "/" : meta.Path,
        Method = string.Equals(meta.Methods?.FirstOrDefault(), "post", StringComparison.OrdinalIgnoreCase) ? "POST" : "GET",
        AuthType = "AK",
        Style = style,
        ReqBodyType = "formData",
        BodyType = "json",
    };

    /// <summary>把凭证与端点写入 SDK 配置，统一 UserAgent。</summary>
    internal static Config BuildConfig(CloudCredentials credentials, string endpoint) => new()
    {
        AccessKeyId = credentials.AccessKeyId,
        AccessKeySecret = credentials.AccessKeySecret,
        SecurityToken = credentials.SecurityToken,
        Endpoint = endpoint,
        Protocol = "HTTPS",
        UserAgent = "alibaba-cloud-ops-mcp-server",
    };

    /// <summary>读取 RegionId，缺省回落到 cn-hangzhou。</summary>
    internal static string ReadRegion(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (parameters.TryGetValue("RegionId", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var region = value.GetString();
            if (!string.IsNullOrWhiteSpace(region))
            {
                return region.ToLowerInvariant();
            }
        }

        return "cn-hangzhou";
    }

    /// <summary>序列化查询值；ECS 的列表参数保留 JSON 数组原文。</summary>
    internal static string SerializeQueryValue(string service, string key, JsonElement value)
    {
        if (string.Equals(service, "ecs", StringComparison.OrdinalIgnoreCase)
            && EcsListParameters.Contains(key)
            && value.ValueKind == JsonValueKind.Array)
        {
            return value.GetRawText();
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText(),
        };
    }

    /// <summary>把 SDK 响应转成 JsonNode，遇到不支持的类型时退化为字符串。</summary>
    internal static JsonNode? ToNode(Dictionary<string, object> response)
    {
        try
        {
            return JsonSerializer.SerializeToNode(response);
        }
        catch (NotSupportedException)
        {
            return JsonNode.Parse(JsonSerializer.Serialize(
                response.ToDictionary(x => x.Key, x => x.Value?.ToString())));
        }
    }
}
