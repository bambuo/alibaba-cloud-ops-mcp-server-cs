using AlibabaCloud.Ops.Mcp.Server.Options;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void Uses_documented_defaults()
    {
        var options = ServerOptions.Parse([]);

        Assert.Equal(TransportKind.Stdio, options.Transport);
        Assert.Equal(8000, options.Port);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(CloudEnvironment.Domestic, options.Environment);
        Assert.False(options.HeadersCredentialOnly);
        Assert.False(options.CodeDeploy);
        Assert.Empty(options.ServiceCodes);
        Assert.Empty(options.VisibleToolNames);
    }

    [Fact]
    public void Parses_all_supported_options()
    {
        var options = ServerOptions.Parse(
        [
            "--transport", "streamable-http",
            "--host", "0.0.0.0",
            "--port", "9000",
            "--services", "ecs,vpc",
            "--env", "international",
            "--headers-credential-only",
            "--code-deploy",
            "--visible-tools", "OOS_RunCommand, LOCAL_ListDirectory",
            "--extra-config", "{\"sls\":[\"GetProject\"]}",
        ]);

        Assert.Equal(TransportKind.StreamableHttp, options.Transport);
        Assert.Equal(9000, options.Port);
        Assert.Equal("0.0.0.0", options.Host);
        Assert.Equal(["ecs", "vpc"], options.ServiceCodes);
        Assert.Equal(CloudEnvironment.International, options.Environment);
        Assert.True(options.HeadersCredentialOnly);
        Assert.True(options.CodeDeploy);
        Assert.Equal(["OOS_RunCommand", "LOCAL_ListDirectory"], options.VisibleToolNames);
    }

    [Fact]
    public void Rejects_sse_transport()
    {
        var exception = Assert.Throws<OptionException>(() => ServerOptions.Parse(["--transport", "sse"]));
        Assert.Contains("UnsupportedTransport", exception.Message);
    }

    [Fact]
    public void Accepts_python_literal_extra_config()
    {
        var options = ServerOptions.Parse(["--extra-config", "{'sls': ['GetProject']}"]);

        Assert.NotNull(options.ExtraConfig);
        var parsed = ExtraConfigParser.Parse(options.ExtraConfig!);
        Assert.Contains("GetProject", parsed["sls"]);
    }

    [Fact]
    public void Ignores_invalid_extra_config_with_warning()
    {
        var options = ServerOptions.Parse(["--extra-config", "{not json"]);

        Assert.Null(options.ExtraConfig);
    }

    [Fact]
    public void Parses_inline_values()
    {
        var options = ServerOptions.Parse(["--port=8123", "--env=domestic"]);

        Assert.Equal(8123, options.Port);
        Assert.Equal(CloudEnvironment.Domestic, options.Environment);
    }

    [Fact]
    public void Rejects_unknown_option()
    {
        Assert.Throws<OptionException>(() => ServerOptions.Parse(["--nope"]));
    }

    [Fact]
    public void Extra_config_normalizes_service_keys()
    {
        var parsed = ExtraConfigParser.Parse("{\"SLS\":[\"GetProject\",\"getproject\"]}");

        Assert.True(parsed.ContainsKey("SLS"));
        Assert.Single(parsed["SLS"]);
    }
}
