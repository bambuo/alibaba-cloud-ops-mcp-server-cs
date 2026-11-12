using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>以 OpenAPI 泛化调用执行任意阿里云 RPC/ROA 接口。</summary>
public class GenericApiCaller(
    ApiMetaClient metaClient,
    EndpointResolver endpointResolver,
    ICredentialProvider credentialProvider,
    TimeProvider timeProvider,
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
    public virtual async Task<JsonNode?> CallAsync(
        string service,
        string api,
        IReadOnlyDictionary<string, JsonElement> parameters,
        string? endpointOverride,
        CancellationToken cancellationToken = default)
    {
        var canonicalService = service.ToLowerInvariant();
        ApiMeta? meta = null;
        try
        {
            meta = await metaClient.GetApiMetaAsync(canonicalService, api, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 上游读取 API 元数据失败时继续以 POST / + RPC 发起调用
            logger.LogWarning("Failed to load API metadata for {Service}:{Api} ({Message})",
                canonicalService, api, exception.Message);
        }

        var version = await metaClient.GetServiceVersionAsync(canonicalService, cancellationToken);
        var style = await metaClient.GetServiceStyleAsync(canonicalService, cancellationToken);
        var regionId = ReadRegion(parameters);

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            Flatten(query, key, PrepareValue(canonicalService, key, value));
        }

        var credentials = credentialProvider.Resolve();
        var config = BuildConfig(credentials, endpointOverride ?? endpointResolver.Resolve(canonicalService, regionId), regionId);
        var request = BuildParams(meta, version, style, api);

        var openApiRequest = new OpenApiRequest { Query = query };
        logger.LogInformation("Calling {Service}.{Api} in {Region}", canonicalService, api, regionId);

        var client = new AlibabaCloud.OpenApiClient.Client(config);
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var response = await client.CallApiAsync(request, openApiRequest, new Darabonba.Models.RuntimeOptions());
                if (response.TryGetValue("statusCode", out var status) && status is int code && code >= 400)
                {
                    var message = response.TryGetValue("body", out var body) ? body?.ToString() : null;
                    throw new CloudApiException($"Http{code}", message ?? "Request failed");
                }

                return ToNode(response);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                last = exception;
                if (attempt < 2 && IsBadFileDescriptor(exception))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), timeProvider, cancellationToken);
                    continue;
                }

                throw CloudErrors.FromSdk(exception);
            }
        }

        throw new OpsException("OpenApiCallFailed", $"OpenAPI call failed: {last?.Message}", null);
    }

    /// <summary>组装调用所需的 Action、Version、路径与 HTTP 方法；元数据缺失时回退 POST /。</summary>
    internal static Params BuildParams(ApiMeta? meta, string version, string style, string api) => new()
    {
        Action = api,
        Version = version,
        Protocol = "HTTPS",
        Pathname = string.IsNullOrWhiteSpace(meta?.Path) ? "/" : meta.Path,
        Method = meta?.Methods is { Count: > 0 } methods
                 && string.Equals(methods[0], "post", StringComparison.OrdinalIgnoreCase)
            ? "POST"
            : meta is null ? "POST" : "GET",
        AuthType = "AK",
        Style = style,
        ReqBodyType = "formData",
        BodyType = "json",
    };

    /// <summary>把凭证、端点、地域与超时写入 SDK 配置，统一 UserAgent。</summary>
    internal static Config BuildConfig(CloudCredentials credentials, string endpoint, string regionId) => new()
    {
        AccessKeyId = credentials.AccessKeyId,
        AccessKeySecret = credentials.AccessKeySecret,
        SecurityToken = credentials.SecurityToken,
        Endpoint = endpoint,
        RegionId = regionId,
        Protocol = "HTTPS",
        UserAgent = "alibaba-cloud-ops-mcp-server",
        ConnectTimeout = 10_000,
        ReadTimeout = 60_000,
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

    /// <summary>ECS 的列表参数按紧凑 JSON 数组字符串下发，其余原样保留。</summary>
    internal static JsonElement PrepareValue(string service, string key, JsonElement value)
    {
        if (string.Equals(service, "ecs", StringComparison.OrdinalIgnoreCase)
            && EcsListParameters.Contains(key)
            && value.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.SerializeToElement(JsonNode.Parse(value.GetRawText())!.ToJsonString());
        }

        return value;
    }

    /// <summary>按 openapiutil 规则展开嵌套参数：对象用 `Key.field`，数组用 `Key.N`（下标从 1 起）。</summary>
    internal static void Flatten(IDictionary<string, string> target, string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    Flatten(target, $"{key}.{property.Name}", property.Value);
                }

                break;
            case JsonValueKind.Array:
                var index = 1;
                foreach (var item in value.EnumerateArray())
                {
                    Flatten(target, $"{key}.{index}", item);
                    index++;
                }

                break;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;
            default:
                var name = key.StartsWith('.') ? key[1..] : key;
                target[name] = ScalarText(value);
                break;
        }
    }

    /// <summary>标量转字符串，与 Java String.valueOf 一致。</summary>
    internal static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

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

    private static bool IsBadFileDescriptor(Exception exception) =>
        exception.Message.Contains("bad file descriptor", StringComparison.OrdinalIgnoreCase);
}