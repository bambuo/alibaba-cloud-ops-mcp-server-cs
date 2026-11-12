using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class CommonToolsTests
{
    [Fact]
    public async Task GetApiInfo_returns_only_parameter_list()
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

            return Json("""{"summary":"查询实例","path":"/","methods":["post"],"parameters":[{"name":"RegionId","in":"query","schema":{"type":"string"}}]}""");
        });
        var meta = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);
        var tools = new CommonTools(ServerOptions.Parse([]), meta, caller: null!);

        var result = await tools.GetApiInfo("ecs", "DescribeInstances");
        var payload = JsonNode.Parse(Text(result)) as JsonArray;

        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal("RegionId", payload![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Only_entity_not_exists_is_treated_as_missing()
    {
        Assert.True(CodeDeployTools.IsMissingEntity(
            new CloudApiException("Http404", "EntityNotExists.ApplicationGroup")));
        Assert.False(CodeDeployTools.IsMissingEntity(
            new CloudApiException("Http403", "Forbidden.RAM")));
        Assert.False(CodeDeployTools.IsMissingEntity(
            new CloudApiException("Http500", "InternalError")));
    }

    [Fact]
    public void Oos_body_unwraps_generic_response()
    {
        var response = JsonNode.Parse("""{"statusCode":200,"body":{"RequestId":"req-1"},"headers":{}}""");
        var body = OosExecutor.Body(response);

        Assert.Equal("req-1", body!["RequestId"]!.GetValue<string>());
    }

    private static string Text(CallToolResult result) =>
        result.Content[0] is TextContentBlock text ? text.Text : string.Empty;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
