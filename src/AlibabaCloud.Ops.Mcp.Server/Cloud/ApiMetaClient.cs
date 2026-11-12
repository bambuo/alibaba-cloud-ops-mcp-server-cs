using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
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

/// <summary>拉取 `api.aliyun.com/meta/v1` 元数据，按产品与 API 缓存结果。</summary>
public sealed class ApiMetaClient(HttpClient httpClient, ILogger<ApiMetaClient> logger)
{
    private const string BaseUrl = "https://api.aliyun.com/meta/v1";
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ProductInfo>>>> _products = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<JsonDocument>>> _overviews = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ApiMeta>>> _apis = new();
    private readonly ConcurrentDictionary<string, string> _standardNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>列出全部云产品（只请求一次，结果缓存）。</summary>
    public async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        await CachedAsync(_products, "products", FetchProductsAsync, cancellationToken);

    /// <summary>返回产品的默认 API 版本。</summary>
    public async Task<string?> GetServiceVersionAsync(string service, CancellationToken cancellationToken = default)
    {
        var products = await GetProductsAsync(cancellationToken);
        return products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase))?.DefaultVersion;
    }

    /// <summary>返回产品的调用风格（RPC/ROA），未知时按 RPC。</summary>
    public async Task<string> GetServiceStyleAsync(string service, CancellationToken cancellationToken = default)
    {
        var products = await GetProductsAsync(cancellationToken);
        return products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase))?.Style ?? "RPC";
    }

    /// <summary>把别名归一为元数据中的标准服务码，未知服务抛 <see cref="CloudApiException"/>。</summary>
    public async Task<string> GetStandardServiceCodeAsync(string service, CancellationToken cancellationToken = default)
    {
        if (_standardNames.TryGetValue(service, out var cached))
        {
            return cached;
        }

        var products = await GetProductsAsync(cancellationToken);
        var match = products.FirstOrDefault(x => string.Equals(x.Code, service, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new CloudApiException("InvalidServiceName", $"Please check the Service ({service}) you provide.");
        }

        _standardNames[service] = match.Code;
        return match.Code;
    }

    /// <summary>列出指定服务下的全部 API 名称。</summary>
    public async Task<IReadOnlyList<string>> GetApiNamesAsync(string service, CancellationToken cancellationToken = default)
    {
        var standard = await GetStandardServiceCodeAsync(service, cancellationToken);
        var version = await GetServiceVersionAsync(standard, cancellationToken)
                      ?? throw new CloudApiException("InvalidServiceName", $"No version found for service ({service}).");

        var overview = await GetOverviewAsync(standard, version, cancellationToken);
        if (!overview.RootElement.TryGetProperty("apis", out var apis) || apis.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return apis.EnumerateObject().Select(x => x.Name).ToList();
    }

    /// <summary>读取单个 API 的元数据（含版本），按服务与 API 缓存。</summary>
    public async Task<ApiMeta> GetApiMetaAsync(string service, string api, CancellationToken cancellationToken = default)
    {
        var key = $"{service.ToLowerInvariant()}/{api.ToLowerInvariant()}";
        return await CachedAsync(_apis, key, token => FetchApiMetaAsync(service, api, token), cancellationToken);
    }

    /// <summary>缓存成功结果；失败项立即移出缓存，避免瞬时故障导致永久不可用。</summary>
    private static async Task<T> CachedAsync<T>(
        ConcurrentDictionary<string, Lazy<Task<T>>> cache,
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var lazy = cache.GetOrAdd(key, _ => new Lazy<Task<T>>(() => factory(cancellationToken)));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, lazy));
            throw;
        }
    }

    /// <summary>请求 products.json 并记录数量。</summary>
    private async Task<IReadOnlyList<ProductInfo>> FetchProductsAsync(CancellationToken cancellationToken)
    {
        var products = await httpClient.GetFromJsonAsync<List<ProductInfo>>(
            $"{BaseUrl}/products.json", cancellationToken) ?? [];
        logger.LogDebug("Loaded {Count} products from OpenAPI metadata", products.Count);
        return products;
    }

    /// <summary>按服务与版本缓存产品 overview（内含 API 清单）。</summary>
    private async Task<JsonDocument> GetOverviewAsync(string service, string version, CancellationToken cancellationToken) =>
        await CachedAsync(
            _overviews,
            $"{service}/{version}",
            token => FetchJsonAsync($"{BaseUrl}/products/{service}/versions/{version}/overview.json", token),
            cancellationToken);

    /// <summary>解析 API 名、版本与路径，加载 api.json。</summary>
    private async Task<ApiMeta> FetchApiMetaAsync(string service, string api, CancellationToken cancellationToken)
    {
        var standard = await GetStandardServiceCodeAsync(service, cancellationToken);
        var version = await GetServiceVersionAsync(standard, cancellationToken)
                      ?? throw new CloudApiException("InvalidServiceName", $"No version found for service ({service}).");

        var overview = await GetOverviewAsync(standard, version, cancellationToken);
        var standardApi = api;
        if (overview.RootElement.TryGetProperty("apis", out var apis) && apis.ValueKind == JsonValueKind.Object)
        {
            standardApi = apis.EnumerateObject()
                .Select(x => x.Name)
                .FirstOrDefault(x => string.Equals(x, api, StringComparison.OrdinalIgnoreCase)) ?? api;
        }

        var url = $"{BaseUrl}/products/{standard}/versions/{version}/apis/{standardApi}/api.json";
        using var document = await FetchJsonAsync(url, cancellationToken);
        var meta = document.RootElement.Deserialize<ApiMeta>(JsonOptions)
                   ?? throw new CloudApiException("InvalidAPIName", $"Please check the API ({api}) you provide.");
        return meta with { Version = version };
    }

    /// <summary>GET 并解析 JSON；404 转换为 InvalidAPIName。</summary>
    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CloudApiException("InvalidAPIName", $"Metadata not found: {url}");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
