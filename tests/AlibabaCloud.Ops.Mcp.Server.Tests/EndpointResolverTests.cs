using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class EndpointResolverTests
{
    private static EndpointResolver Resolver(params string[] args) =>
        new(ServerOptions.Parse(args));

    [Theory]
    [InlineData("ecs", "cn-hangzhou", "ecs.cn-hangzhou.aliyuncs.com")]
    [InlineData("oos", "cn-beijing", "oos.cn-beijing.aliyuncs.com")]
    [InlineData("vpc", "cn-shanghai", "vpc.cn-shanghai.aliyuncs.com")]
    [InlineData("rds", "cn-hangzhou", "rds.aliyuncs.com")]
    [InlineData("rds", "cn-chengdu", "rds.cn-chengdu.aliyuncs.com")]
    [InlineData("r-kvstore", "cn-shenzhen", "r-kvstore.aliyuncs.com")]
    [InlineData("cbn", "cn-hangzhou", "cbn.aliyuncs.com")]
    [InlineData("ros", "cn-hangzhou", "ros.aliyuncs.com")]
    [InlineData("unknown", "cn-hangzhou", "unknown.cn-hangzhou.aliyuncs.com")]
    [InlineData("bssopenapi", "cn-hangzhou", "business.aliyuncs.com")]
    [InlineData("bssopenapi", "cn-chengdu", "business.aliyuncs.com")]
    public void Resolves_endpoint_by_service_and_region(string service, string region, string expected)
    {
        Assert.Equal(expected, Resolver().Resolve(service, region));
    }

    [Fact]
    public void International_environment_uses_international_billing_endpoint()
    {
        Assert.Equal("business.ap-southeast-1.aliyuncs.com",
            Resolver("--env", "international").Resolve("bssopenapi", "cn-hangzhou"));
    }

    [Fact]
    public void Service_and_region_are_case_insensitive()
    {
        Assert.Equal("ecs.cn-hangzhou.aliyuncs.com", Resolver().Resolve("ECS", "CN-Hangzhou"));
    }
}
