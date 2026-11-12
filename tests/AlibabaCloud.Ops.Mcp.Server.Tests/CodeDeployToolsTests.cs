using AlibabaCloud.Ops.Mcp.Server.Tools;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class CodeDeployToolsTests
{
    [Fact]
    public void Start_command_required_returns_upstream_guidance()
    {
        var payload = CodeDeployTools.BuildStartCommandRequired("/tmp/app.tar.gz", "java");

        Assert.Equal("START_COMMAND_REQUIRED", payload["error"]!.GetValue<string>());
        Assert.Equal("/tmp/app.tar.gz", payload["file_path"]!.GetValue<string>());
        Assert.Equal("java", payload["deploy_language"]!.GetValue<string>());
        Assert.Contains("application_start", payload["message"]!.GetValue<string>());
        var instructions = payload["instructions"]!.GetValue<string>();
        Assert.Contains("启动命令生成失败", instructions);
        Assert.Contains("nohup", instructions);
    }

    [Fact]
    public void Oss_service_unavailable_returns_activation_guidance()
    {
        var payload = CodeDeployTools.BuildOssNotActivated();

        Assert.Equal("OSS_SERVICE_NOT_ACTIVATED", payload["error"]!.GetValue<string>());
        Assert.Equal("https://oss.console.aliyun.com/", payload["oss_console_link"]!.GetValue<string>());
        Assert.Contains("oss.console.aliyun.com", payload["instructions"]!.GetValue<string>());
    }
}
