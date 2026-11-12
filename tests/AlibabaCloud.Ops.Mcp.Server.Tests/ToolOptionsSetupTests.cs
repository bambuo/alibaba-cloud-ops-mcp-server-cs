using System.Net;
using System.Text;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Mcp;
using AlibabaCloud.Ops.Mcp.Server.Options;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class ToolOptionsSetupTests
{
    [Fact]
    public async Task Dynamic_tools_are_reapplied_to_each_new_collection()
    {
        var options = ServerOptions.Parse(["--visible-tools", "ECS_DescribeInstances"]);
        var registry = CreateRegistry(options);
        await registry.RegisterAsync(CancellationToken.None);
        var setup = new ToolOptionsSetup(options, registry);

        var first = new McpServerOptions();
        var second = new McpServerOptions();
        setup.PostConfigure(null, first);
        setup.PostConfigure(null, second);

        Assert.Equal(["ECS_DescribeInstances"], Names(first));
        Assert.Equal(["ECS_DescribeInstances"], Names(second));
    }

    [Fact]
    public void Whitelist_removes_tools_outside_the_allow_list()
    {
        var options = ServerOptions.Parse(["--visible-tools", "LOCAL_ListDirectory"]);
        var setup = new ToolOptionsSetup(options, CreateRegistry(options));

        var mcpOptions = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
            {
                FakeTool("LOCAL_ListDirectory"),
                FakeTool("OOS_RunCommand"),
            },
        };
        setup.PostConfigure(null, mcpOptions);

        Assert.Equal(["LOCAL_ListDirectory"], Names(mcpOptions));
    }

    [Fact]
    public void Code_deploy_mode_keeps_registered_tools()
    {
        var options = ServerOptions.Parse(["--code-deploy"]);
        var setup = new ToolOptionsSetup(options, CreateRegistry(options));

        var mcpOptions = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
            {
                FakeTool("CodeDeploy_Deploy"),
                FakeTool("LOCAL_RunShellScript"),
            },
        };
        setup.PostConfigure(null, mcpOptions);

        Assert.Equal(2, mcpOptions.ToolCollection.Count);
    }

    private static IReadOnlyList<string> Names(McpServerOptions options) =>
        options.ToolCollection!.Select(tool => tool.ProtocolTool.Name).Order().ToList();

    private static McpServerTool FakeTool(string name) =>
        McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = name });

    private static DynamicToolRegistry CreateRegistry(ServerOptions options)
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/products.json"))
            {
                return Json("""[{"code":"ecs","name":"ECS","defaultVersion":"2014-05-26","style":"RPC"}]""");
            }

            if (path.Contains("/apis.json") || path.Contains("/overview.json"))
            {
                return Json("""{"apis":{"DescribeInstances":{}}}""");
            }

            return Json("""{"summary":"查询实例","path":"/","methods":["post"],"parameters":[{"name":"RegionId","in":"query","schema":{"type":"string"}}]}""");
        });

        var meta = new ApiMetaClient(new HttpClient(handler), NullLogger<ApiMetaClient>.Instance);
        var caller = new GenericApiCaller(
            meta,
            new EndpointResolver(options),
            new StaticCredentials(),
            NullLogger<GenericApiCaller>.Instance);
        var factory = new DynamicApiToolFactory(meta, caller, NullLogger<DynamicApiToolFactory>.Instance);

        return new DynamicToolRegistry(options, factory, NullLogger<DynamicToolRegistry>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StaticCredentials : ICredentialProvider
    {
        public CloudCredentials Resolve() => new("ak", "sk", null, "test");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
