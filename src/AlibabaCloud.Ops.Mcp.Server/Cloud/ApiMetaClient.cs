using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>OpenAPI 元数据中的产品条目。</summary>
public sealed record ProductInfo(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("defaultVersion")] string DefaultVersion,
    [property: JsonPropertyName("style")] string? Style);

/// <summary>API 的单个入参定义。</summary>
public sealed record ApiParameter(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("in")] string? In,
    [property: JsonPropertyName("schema")] JsonElement Schema);

/// <summary>动态工具所需的 API 元数据，Version 由产品元数据补充。</summary>
public sealed record ApiMeta(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("methods")] List<string>? Methods,
    [property: JsonPropertyName("style")] string? Style,
    [property: JsonPropertyName("parameters")] List<ApiParameter>? Parameters)
{
    /// <summary>API 版本号，例如 2014-05-26。</summary>
    [JsonIgnore]
    public string Version { get; init; } = string.Empty;
}

/// <summary>拉取 `api.aliyun.com/meta/v1` 元数据，按路径缓存并带 TTL 与退避重试。</summary>
public sealed class ApiMetaClient(
    HttpClient httpClient,
    ServerOptions options,
    TimeProvider timeProvider,
    ILogger<ApiMetaClient> logger)
{
    private const string BaseUrl = "https://api.aliyun.com/meta/v1";
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.MetaCacheTtlSeconds);

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, JsonNode? Value);

    /// <summary>列出全部云产品。</summary>
    public async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        var node = await FetchAsync("products.json", cancellationToken);
        return node?.Deserialize<List<ProductInfo>>(JsonOptions) ?? [];
    }

    /// <summary>返回产品的默认 API 版本，未知服务抛 InvalidServiceName。</summary>
    public async Task<string> GetServiceVersionAsync(string service, CancellationToken cancellationToken = default)
    {
        var products = await GetProductsAsync(cancellationToken);
        var match = products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase));
        return match?.DefaultVersion ?? throw InvalidService(service);
    }

    /// <summary>返回产品的调用风格（RPC/ROA），空白或未知时按 RPC。</summary>
    public async Task<string> GetServiceStyleAsync(string service, CancellationToken cancellationToken = default)
    {
        var products = await GetProductsAsync(cancellationToken);
        var style = products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase))?.Style;
        return string.IsNullOrWhiteSpace(style) ? "RPC" : style;
    }

    /// <summary>把别名归一为元数据中的标准服务码，未知服务抛 InvalidServiceName。</summary>
    public async Task<string> GetStandardServiceCodeAsync(string service, CancellationToken cancellationToken = default)
    {
        var products = await GetProductsAsync(cancellationToken);
        var match = products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase))
                    ?? throw InvalidService(service);
        return match.Code;
    }

    /// <summary>列出指定服务下的全部 API 名称。</summary>
    public async Task<IReadOnlyList<string>> GetApiNamesAsync(string service, CancellationToken cancellationToken = default)
    {
        var standard = await GetStandardServiceCodeAsync(service, cancellationToken);
        var version = await GetServiceVersionAsync(standard, cancellationToken);
        var overview = await FetchAsync($"products/{standard}/versions/{version}/overview.json", cancellationToken);
        if (overview is not JsonObject obj || obj["apis"] is not JsonObject apis)
        {
            return [];
        }

        return apis.Select(x => x.Key).ToList();
    }

    /// <summary>返回 API 的标准名称（大小写与元数据一致），未找到抛 InvalidAPIName。</summary>
    public async Task<string> GetStandardApiAsync(string service, string api, CancellationToken cancellationToken = default)
    {
        var names = await GetApiNamesAsync(service, cancellationToken);
        return names.FirstOrDefault(x => string.Equals(x, api, StringComparison.OrdinalIgnoreCase))
               ?? throw new CloudApiException("InvalidAPIName",
                   $"InvalidAPIName: Please check the Service ({service.ToLowerInvariant()}) and the API ({api}) you provide.");
    }

    /// <summary>读取单个 API 的元数据（含版本）。</summary>
    public async Task<ApiMeta> GetApiMetaAsync(string service, string api, CancellationToken cancellationToken = default)
    {
        var standard = await GetStandardServiceCodeAsync(service, cancellationToken);
        var standardApi = await GetStandardApiAsync(standard, api, cancellationToken);
        var version = await GetServiceVersionAsync(standard, cancellationToken);
        var node = await FetchAsync(
            $"products/{standard}/versions/{version}/apis/{standardApi}/api.json", cancellationToken);
        var meta = node?.Deserialize<ApiMeta>(JsonOptions)
                   ?? throw new CloudApiException("DynamicMetaUnavailable", $"Empty metadata for {standard}.{standardApi}");
        return meta with { Version = version };
    }

    /// <summary>按路径拉取元数据：命中未过期缓存直接返回，否则最多重试 3 次并线性退避。</summary>
    private async Task<JsonNode?> FetchAsync(string path, CancellationToken cancellationToken)
    {
        var key = path.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            return cached.Value;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync($"{BaseUrl}/{path}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CloudApiException("DynamicMetaUnavailable",
                        $"Metadata request failed with status {(int)response.StatusCode}: {path}");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var node = JsonNode.Parse(body);
                _cache[key] = new CacheEntry(timeProvider.GetUtcNow() + _ttl, node);
                logger.LogDebug("Loaded metadata {Path}", path);
                return node;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                last = exception;
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), timeProvider, cancellationToken);
                }
            }
        }

        throw new CloudApiException("DynamicMetaUnavailable",
            $"Failed to load metadata from {path}: {last?.Message ?? "unknown error"}");
    }

    private static CloudApiException InvalidService(string service) =>
        new("InvalidServiceName",
            $"InvalidServiceName: Please check the Service ({service.ToLowerInvariant()}) you provide.");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
