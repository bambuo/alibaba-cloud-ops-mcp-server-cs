using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class CmsToolsTests
{
    [Fact]
    public async Task Uses_upstream_metric_names_and_per_instance_dimensions()
    {
        var (tools, caller) = Create();
        var ids = new List<string> { "i-1", "i-2" };

        await tools.GetCpuUsageData(ids, "cn-beijing");
        AssertCalled(caller, "cpu_total", ids);

        await tools.GetCpuLoadavgData(ids, "cn-beijing");
        AssertCalled(caller, "load_1m", ids);

        await tools.GetCpuloadavg5mData(ids, "cn-beijing");
        AssertCalled(caller, "load_5m", ids);

        await tools.GetCpuloadavg15mData(ids, "cn-beijing");
        AssertCalled(caller, "load_15m", ids);

        await tools.GetMemUsedData(ids, "cn-beijing");
        AssertCalled(caller, "memory_usedspace", ids);

        await tools.GetMemUsageData(ids, "cn-beijing");
        AssertCalled(caller, "memory_usedutilization", ids);

        await tools.GetDiskUsageData(ids, "cn-beijing");
        AssertCalled(caller, "diskusage_utilization", ids);

        await tools.GetDiskTotalData(ids, "cn-beijing");
        AssertCalled(caller, "diskusage_total", ids);

        await tools.GetDiskUsedData(ids, "cn-beijing");
        AssertCalled(caller, "diskusage_used", ids);
    }

    private static void AssertCalled(RecordingCaller caller, string metric, IReadOnlyList<string> ids)
    {
        Assert.Equal("cms", caller.Service);
        Assert.Equal("DescribeMetricLast", caller.Api);
        Assert.Equal(metric, caller.Last!["MetricName"].GetString());
        Assert.Equal("acs_ecs_dashboard", caller.Last["Namespace"].GetString());
        Assert.Equal("metrics.cn-beijing.aliyuncs.com", caller.EndpointOverride);

        var dimensions = caller.Last["Dimensions"].GetString()!;
        foreach (var id in ids)
        {
            Assert.Contains(id, dimensions);
        }
    }

    private static (CmsTools Tools, RecordingCaller Caller) Create()
    {
        var options = ServerOptions.Parse([]);
        var meta = new ApiMetaClient(new HttpClient(), options, TimeProvider.System, NullLogger<ApiMetaClient>.Instance);
        var caller = new RecordingCaller(meta, new EndpointResolver(options));
        return (new CmsTools(caller, NullLogger<CmsTools>.Instance), caller);
    }

    private sealed class RecordingCaller(ApiMetaClient meta, EndpointResolver resolver)
        : GenericApiCaller(meta, resolver, new StaticCredentials(), TimeProvider.System, NullLogger<GenericApiCaller>.Instance)
    {
        public string? Service { get; private set; }

        public string? Api { get; private set; }

        public string? EndpointOverride { get; private set; }

        public Dictionary<string, JsonElement>? Last { get; private set; }

        public override Task<JsonNode?> CallAsync(
            string service,
            string api,
            IReadOnlyDictionary<string, JsonElement> parameters,
            string? endpointOverride,
            CancellationToken cancellationToken = default)
        {
            Service = service;
            Api = api;
            EndpointOverride = endpointOverride;
            Last = new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<JsonNode?>(JsonNode.Parse("""{"Datapoints":"[]"}"""));
        }
    }

    private sealed class StaticCredentials : ICredentialProvider
    {
        public CloudCredentials Resolve() => new("ak", "sk", null, "test");

        public CloudCredentials ResolveForOss() => Resolve();
    }
}
