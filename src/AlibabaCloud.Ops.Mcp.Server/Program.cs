using System.Reflection;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Mcp;
using AlibabaCloud.Ops.Mcp.Server.Options;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

try
{
    var options = ServerOptions.Parse(args);
    if (options.Help)
    {
        Console.Out.WriteLine(ServerOptions.Usage);
        return 0;
    }

    if (options.CodeDeploy && options.VisibleToolNames.Count > 0)
    {
        Console.Error.WriteLine("WARN: --code-deploy takes precedence over --visible-tools; whitelist ignored");
    }

    return options.Transport switch
    {
        TransportKind.Stdio => await RunStdioAsync(options),
        TransportKind.StreamableHttp => await RunHttpAsync(options),
        _ => await RunBothAsync(options),
    };
}
catch (OptionException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(ServerOptions.Usage);
    return 2;
}

// 注册配置、凭证、云调用、工具与会话选项后置处理。
static void ConfigureServices(IServiceCollection services, ServerOptions options)
{
    services.AddSingleton(options);
    services.AddHttpContextAccessor();
    services.AddHttpClient<ApiMetaClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });
    services.AddSingleton<EndpointResolver>();
    services.AddSingleton<ICredentialProvider, CredentialProvider>();
    services.AddSingleton<IDefaultCredentialChain, AliyunDefaultCredentialChain>();
    services.AddSingleton<GenericApiCaller>();
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<OosExecutor>();
    services.AddSingleton<DynamicApiToolFactory>();
    services.AddSingleton<DynamicToolRegistry>();
    services.AddSingleton<IPostConfigureOptions<McpServerOptions>, ToolOptionsSetup>();
    services.AddSingleton<OosTools>();
    services.AddSingleton<CmsTools>();
    services.AddSingleton<OssTools>();
    services.AddSingleton<LocalTools>();
    services.AddSingleton<CodeDeployTools>();
    services.AddSingleton<InstanceTools>();
    services.AddSingleton<CommonTools>();
}

// 创建 MCP 服务并写入服务名、版本与 instructions。
static IMcpServerBuilder AddMcpServer(IServiceCollection services)
{
    return services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "alibaba-cloud-ops-mcp-server",
            Version = ServerVersion(),
        };
        options.ServerInstructions = ServerInstructions.Text;
    });
}

// 版本只维护 csproj 的 <Version>，与上游发布号保持一致。
static string ServerVersion() =>
    (typeof(ServerOptions).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
     ?? "0.0.0").Split('+')[0];

// 按启动模式注册静态工具。
static void RegisterStaticTools(IMcpServerBuilder mcp, ServerOptions options)
{
    if (options.CodeDeploy)
    {
        mcp.WithTools<CodeDeployTools>()
            .WithTools<InstanceTools>();
        if (options.EnableLocalTools)
        {
            mcp.WithTools<LocalTools>();
        }

        return;
    }

    mcp.WithTools<OosTools>()
        .WithTools<CmsTools>()
        .WithTools<OssTools>()
        .WithTools<CodeDeployTools>();

    if (options.EnableLocalTools)
    {
        mcp.WithTools<LocalTools>();
    }

    // 上游 visible-tools 模式会加载 application_management_tools，含静态 ECS_DescribeInstances；
    // 白名单过滤在 PostConfigure 完成，未命中时会被移除。
    if (options.VisibleToolNames.Count > 0)
    {
        mcp.WithTools<InstanceTools>();
    }

    if (options.ServiceCodes.Count > 0)
    {
        mcp.WithTools<CommonTools>();
    }
}

// 日志统一走 stderr，避免污染 stdio 协议帧。
static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    logging.SetMinimumLevel(LogLevel.Information);
}

// 动态工具依赖异步元数据；注册完成后需回填到已生成的 McpServerOptions。
static async Task RegisterDynamicToolsAsync(IServiceProvider services)
{
    await services.GetRequiredService<DynamicToolRegistry>().RegisterAsync(CancellationToken.None);
    var setup = services.GetRequiredService<IPostConfigureOptions<McpServerOptions>>();
    setup.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, services.GetRequiredService<IOptions<McpServerOptions>>().Value);
}

// stdio 传输：注册动态工具后运行宿主。
static async Task<int> RunStdioAsync(ServerOptions options)
{
    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
    ConfigureLogging(builder.Logging);
    ConfigureServices(builder.Services, options);
    var mcp = AddMcpServer(builder.Services);
    RegisterStaticTools(mcp, options);
    mcp.WithStdioServerTransport();
    var host = builder.Build();
    await RegisterDynamicToolsAsync(host.Services);
    await host.RunAsync();
    return 0;
}

// Streamable HTTP 传输：注册动态工具后再映射 /mcp 端点。
static async Task<int> RunHttpAsync(ServerOptions options)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
    ConfigureLogging(builder.Logging);
    builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
    ConfigureServices(builder.Services, options);
    var mcp = AddMcpServer(builder.Services);
    RegisterStaticTools(mcp, options);
    mcp.WithHttpTransport(transport => transport.Stateless = false);

    var app = builder.Build();
    await RegisterDynamicToolsAsync(app.Services);
    app.MapMcp("/mcp");
    await app.RunAsync();
    return 0;
}

// both 传输：同进程并行提供 stdio 与 Streamable HTTP，两者工具面一致。
static async Task<int> RunBothAsync(ServerOptions options)
{
    var http = RunHttpAsync(options);
    var stdio = RunStdioAsync(options);
    await Task.WhenAll(http, stdio);
    return 0;
}
