using System.Text.Json;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class RequestBuilderTests
{
    [Fact]
    public void Builds_post_request_from_metadata()
    {
        var meta = new ApiMeta("summary", "/api/v1/clusters", ["post"], "ROA", []) { Version = "2015-12-15" };

        var request = GenericApiCaller.BuildParams(meta, "ROA", "DescribeClustersV1");

        Assert.Equal("DescribeClustersV1", request.Action);
        Assert.Equal("2015-12-15", request.Version);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v1/clusters", request.Pathname);
        Assert.Equal("ROA", request.Style);
        Assert.Equal("formData", request.ReqBodyType);
    }

    [Fact]
    public void Defaults_to_get_and_root_path()
    {
        var meta = new ApiMeta(null, null, null, null, []) { Version = "2020-01-01" };

        var request = GenericApiCaller.BuildParams(meta, "RPC", "DescribeThings");

        Assert.Equal("GET", request.Method);
        Assert.Equal("/", request.Pathname);
    }

    [Fact]
    public void Builds_config_from_credentials()
    {
        var credentials = new CloudCredentials("id", "secret", "token", "test");

        var config = GenericApiCaller.BuildConfig(credentials, "ecs.cn-hangzhou.aliyuncs.com");

        Assert.Equal("id", config.AccessKeyId);
        Assert.Equal("secret", config.AccessKeySecret);
        Assert.Equal("token", config.SecurityToken);
        Assert.Equal("ecs.cn-hangzhou.aliyuncs.com", config.Endpoint);
        Assert.Equal("alibaba-cloud-ops-mcp-server", config.UserAgent);
    }
}
