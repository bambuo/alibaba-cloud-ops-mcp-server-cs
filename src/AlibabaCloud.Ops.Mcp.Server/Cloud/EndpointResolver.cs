using AlibabaCloud.Ops.Mcp.Server.Options;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>按服务码、地域与云环境计算 OpenAPI 端点。</summary>
public sealed class EndpointResolver(ServerOptions options)
{
    private static readonly HashSet<string> RegionEndpointServices =
        new(StringComparer.OrdinalIgnoreCase) { "ecs", "oos", "vpc", "slb" };

    private static readonly HashSet<string> CentralServices =
        new(StringComparer.OrdinalIgnoreCase) { "cbn", "ros", "ram" };

    private static readonly Dictionary<string, HashSet<string>> DoubleEndpointRegions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["rds"] = RegionSet("cn-qingdao", "cn-beijing", "cn-hangzhou", "cn-shanghai", "cn-shenzhen", "cn-heyuan", "cn-guangzhou", "cn-hongkong"),
            ["ess"] = RegionSet("cn-qingdao", "cn-beijing", "cn-hangzhou", "cn-shanghai", "cn-nanjing", "cn-shenzhen"),
            ["dds"] = RegionSet("cn-qingdao", "cn-beijing", "cn-wulanchabu", "cn-hangzhou", "cn-shanghai", "cn-shenzhen", "cn-heyuan", "cn-guangzhou"),
            ["r-kvstore"] = RegionSet("cn-qingdao", "cn-beijing", "cn-wulanchabu", "cn-hangzhou", "cn-shanghai", "cn-shenzhen", "cn-heyuan"),
        };

    private static readonly HashSet<string> BssDomesticRegions =
        RegionSet("cn-qingdao", "cn-beijing", "cn-zhangjiakou", "cn-huhehaote", "cn-wulanchabu", "cn-hangzhou", "cn-shanghai", "cn-shenzhen", "cn-chengdu", "cn-hongkong");

    /// <summary>解析端点：bssopenapi 与中心化服务特殊处理，其余按地域拼接。</summary>
    public string Resolve(string service, string regionId)
    {
        service = service.ToLowerInvariant();
        regionId = regionId.ToLowerInvariant();

        if (service == "bssopenapi")
        {
            if (options.Environment == CloudEnvironment.International)
            {
                return "business.ap-southeast-1.aliyuncs.com";
            }

            // 与上游一致：domestic 环境不区分地域，一律使用国内端点。
            return BssDomesticRegions.Contains(regionId) || options.Environment == CloudEnvironment.Domestic
                ? "business.aliyuncs.com"
                : "business.ap-southeast-1.aliyuncs.com";
        }

        if (CentralServices.Contains(service))
        {
            return $"{service}.aliyuncs.com";
        }

        if (RegionEndpointServices.Contains(service))
        {
            return $"{service}.{regionId}.aliyuncs.com";
        }

        if (DoubleEndpointRegions.TryGetValue(service, out var regions))
        {
            return regions.Contains(regionId)
                ? $"{service}.aliyuncs.com"
                : $"{service}.{regionId}.aliyuncs.com";
        }

        return $"{service}.{regionId}.aliyuncs.com";
    }

    /// <summary>构造忽略大小写的地域集合。</summary>
    private static HashSet<string> RegionSet(params string[] regions) =>
        new(regions, StringComparer.OrdinalIgnoreCase);
}
