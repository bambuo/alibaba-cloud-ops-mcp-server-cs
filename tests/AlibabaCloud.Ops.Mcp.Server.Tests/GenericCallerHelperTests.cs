using System.Text.Json;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Tools;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class GenericCallerHelperTests
{
    [Fact]
    public void Ecs_list_parameters_are_json_encoded()
    {
        using var document = JsonDocument.Parse("""["i-1","i-2"]""");

        var prepared = GenericApiCaller.PrepareValue("ecs", "InstanceIds", document.RootElement);

        Assert.Equal("""["i-1","i-2"]""", GenericApiCaller.ScalarText(prepared));
    }

    [Fact]
    public void Ecs_list_json_is_compact()
    {
        using var document = JsonDocument.Parse("""[ "i-1", "i-2" ]""");

        var prepared = GenericApiCaller.PrepareValue("ecs", "InstanceIds", document.RootElement);

        Assert.Equal("""["i-1","i-2"]""", GenericApiCaller.ScalarText(prepared));
    }

    [Fact]
    public void Non_ecs_arrays_are_flattened_with_one_based_index()
    {
        using var document = JsonDocument.Parse("""["a","b"]""");
        var query = new Dictionary<string, string>();

        GenericApiCaller.Flatten(query, "Any", document.RootElement);

        Assert.Equal("a", query["Any.1"]);
        Assert.Equal("b", query["Any.2"]);
    }

    [Fact]
    public void Nested_structures_are_flattened_like_openapiutil()
    {
        using var document = JsonDocument.Parse("""{"Tag":[{"Key":"k","Value":"v"}],"Obj":{"field":1}}""");
        var query = new Dictionary<string, string>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            GenericApiCaller.Flatten(query, property.Name, property.Value);
        }

        Assert.Equal("k", query["Tag.1.Key"]);
        Assert.Equal("v", query["Tag.1.Value"]);
        Assert.Equal("1", query["Obj.field"]);
    }

    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("true", "true")]
    [InlineData("42", "42")]
    public void Scalars_are_serialized_as_strings(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, GenericApiCaller.ScalarText(document.RootElement));
    }

    [Fact]
    public void Region_defaults_to_hangzhou()
    {
        Assert.Equal("cn-hangzhou", GenericApiCaller.ReadRegion(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void Region_is_read_and_lowercased()
    {
        using var document = JsonDocument.Parse("\"CN-Beijing\"");
        var parameters = new Dictionary<string, JsonElement> { ["RegionId"] = document.RootElement };

        Assert.Equal("cn-beijing", GenericApiCaller.ReadRegion(parameters));
    }

    [Fact]
    public void Region_whitespace_falls_back_to_hangzhou()
    {
        using var document = JsonDocument.Parse("\"   \"");
        var parameters = new Dictionary<string, JsonElement> { ["RegionId"] = document.RootElement };

        Assert.Equal("cn-hangzhou", GenericApiCaller.ReadRegion(parameters));
    }

    [Fact]
    public void False_is_serialized_as_lowercase_literal()
    {
        using var document = JsonDocument.Parse("false");

        Assert.Equal("false", GenericApiCaller.ScalarText(document.RootElement));
    }

    [Fact]
    public void Response_is_converted_to_json_node()
    {
        var node = GenericApiCaller.ToNode(new Dictionary<string, object>
        {
            ["statusCode"] = 200,
            ["body"] = new Dictionary<string, object> { ["RequestId"] = "req-1" },
        });

        Assert.Equal(200, node!["statusCode"]!.GetValue<int>());
        Assert.Equal("req-1", node["body"]!["RequestId"]!.GetValue<string>());
    }

    [Fact]
    public void Unsupported_response_values_fall_back_to_strings()
    {
        var node = GenericApiCaller.ToNode(new Dictionary<string, object>
        {
            ["statusCode"] = 200,
            ["payload"] = typeof(string),
        });

        Assert.Equal("200", node!["statusCode"]!.GetValue<string>());
        Assert.Equal("System.String", node["payload"]!.GetValue<string>());
    }

    [Fact]
    public void Finds_nested_values_case_insensitively()
    {
        var node = JsonDocument.Parse("""{"body":{"Execution":{"executionId":"exec-1"}}}""").RootElement;

        Assert.Equal("exec-1", ToolResults.Find(System.Text.Json.Nodes.JsonNode.Parse(node.GetRawText()), "ExecutionId")!.GetValue<string>());
    }
}
