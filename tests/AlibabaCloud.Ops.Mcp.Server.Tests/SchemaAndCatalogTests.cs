using System.Reflection;
using System.Text.Json;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class SchemaAndCatalogTests
{
    [Fact]
    public void Builds_schema_from_api_metadata()
    {
        var meta = new ApiMeta(
            "查询实例",
            "/",
            ["post"],
            "RPC",
            [
                Parameter("InstanceIds", """{"type":"string","required":true,"description":"ID list","example":"[\"i-1\"]"}"""),
                Parameter("MaxResults", """{"type":"integer","description":"limit"}"""),
                Parameter("Nested.Value", """{"type":"string"}"""),
            ]);

        var schema = DynamicApiToolFactory.BuildInputSchema("ecs", meta);
        var json = schema.ToJsonString();

        Assert.Contains("\"InstanceIds\"", json);
        Assert.Equal("array", schema["properties"]!["InstanceIds"]!["type"]!.GetValue<string>());
        Assert.Contains("参数类型", schema["properties"]!["InstanceIds"]!["description"]!.GetValue<string>());
        Assert.Contains("\"required\":[\"InstanceIds\"]", json);
        Assert.DoesNotContain("Nested.Value", json);
        Assert.Contains("\"RegionId\"", json);
        Assert.Equal("cn-hangzhou", schema["properties"]!["RegionId"]!["default"]!.GetValue<string>());
    }

    [Fact]
    public void Catalog_contains_expected_static_tools()
    {
        var types = new[]
        {
            typeof(OosTools), typeof(CmsTools), typeof(OssTools), typeof(LocalTools),
            typeof(CodeDeployTools), typeof(InstanceTools), typeof(CommonTools),
        };

        var names = types
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToList();

        Assert.Equal(34, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("OOS_RunCommand", names);
        Assert.Contains("CMS_GetCpuUsageData", names);
        Assert.Contains("OSS_ListBuckets", names);
        Assert.Contains("LOCAL_RunShellScript", names);
        Assert.Contains("OOS_CodeDeploy", names);
        Assert.Contains("ECS_DescribeInstances", names);
        Assert.Contains("CommonAPICaller", names);
    }

    [Fact]
    public void Preserves_upstream_parameter_names()
    {
        var method = typeof(OosTools).GetMethod(nameof(OosTools.StopInstances))!;
        var parameters = method.GetParameters().Select(parameter => parameter.Name).ToList();

        Assert.Contains("InstanceIds", parameters);
        Assert.Contains("ForeceStop", parameters);
    }

    private static ApiParameter Parameter(string name, string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new ApiParameter(name, "query", document.RootElement.Clone());
    }
}
