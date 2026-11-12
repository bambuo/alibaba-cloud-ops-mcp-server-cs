using System.Net;
using System.Text;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class ApiMetaClientTests
{
    [Fact]
    public async Task Loads_and_caches_api_metadata()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/products.json"))
            {
                return Json("""[{"code":"ecs","name":"ECS","defaultVersion":"2014-05-26","style":"RPC"}]""");
            }

            if (path.Contains("/overview.json"))
            {
                return Json("""{"apis":{"DescribeInstances":{}}}""");
            }

            if (path.Contains("/apis/DescribeInstances/api.json"))
            {
                return Json("""{"summary":"查询实例","path":"/","methods":["post"],"parameters":[{"name":"RegionId","in":"query","schema":{"type":"string"}}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);

        var meta = await client.GetApiMetaAsync("ecs", "DescribeInstances");
        var again = await client.GetApiMetaAsync("ecs", "DescribeInstances");

        Assert.Equal("查询实例", meta.Summary);
        Assert.Equal("2014-05-26", meta.Version);
        Assert.Single(meta.Parameters!);
        Assert.Same(meta, again);
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Lists_api_names()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/products.json")
            ? Json("""[{"code":"vpc","name":"VPC","defaultVersion":"2016-04-28","style":"RPC"}]""")
            : Json("""{"apis":{"DescribeVpcs":{},"DescribeVSwitches":{}}}"""));

        var client = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);

        var apis = await client.GetApiNamesAsync("vpc");

        Assert.Equal(["DescribeVpcs", "DescribeVSwitches"], apis);
    }

    [Fact]
    public async Task Rejects_unknown_service()
    {
        var handler = new StubHandler(_ => Json("""[]"""));

        var client = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);

        await Assert.ThrowsAsync<CloudApiException>(() => client.GetApiNamesAsync("nope"));
    }

    [Fact]
    public async Task Retries_after_transient_metadata_failure()
    {
        var calls = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json("""[{"code":"ecs","name":"ECS","defaultVersion":"2014-05-26","style":"RPC"}]"""));

        var client = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProductsAsync());
        var products = await client.GetProductsAsync();

        Assert.Single(products);
        Assert.Equal(2, handler.Count);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(handler(request));
        }
    }
}
