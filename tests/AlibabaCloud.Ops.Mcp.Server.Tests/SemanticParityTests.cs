using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Mcp;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

/// <summary>锁定与 Java 侧对齐后的语义：错误契约、实例校验、参数展开、轮询与落盘。</summary>
public sealed class SemanticParityTests
{
    [Fact]
    public void Error_payload_is_structured_json_with_request_id()
    {
        var result = ToolResults.Error("Execution.Failed", "boom", "req-1");

        Assert.True(result.IsError);
        var body = JsonNode.Parse(Text(result))!;
        Assert.Equal("Execution.Failed", body["Code"]!.GetValue<string>());
        Assert.Equal("boom", body["Message"]!.GetValue<string>());
        Assert.Equal("req-1", body["RequestId"]!.GetValue<string>());
    }

    [Fact]
    public void Error_payload_omits_blank_request_id()
    {
        var body = JsonNode.Parse(Text(ToolResults.Error("InvalidParameter", "bad")))!;

        Assert.Null(body["RequestId"]);
    }

    [Fact]
    public void Coded_exception_keeps_its_code()
    {
        var body = JsonNode.Parse(Text(ToolResults.Error(new OpsException("Timeout", "late", "req-2"))))!;

        Assert.Equal("Timeout", body["Code"]!.GetValue<string>());
        Assert.Equal("late", body["Message"]!.GetValue<string>());
        Assert.Equal("req-2", body["RequestId"]!.GetValue<string>());
    }

    [Fact]
    public void Instance_check_matches_exact_ids_only()
    {
        var response = JsonNode.Parse("""
            {"body":{"Instances":{"Instance":[{"InstanceId":"i-abcdef"},{"InstanceId":"i-xyz"}]}}}
            """);

        Assert.True(CodeDeployTools.ContainsInstance(response, "i-abcdef"));
        Assert.False(CodeDeployTools.ContainsInstance(response, "i-abc"));
        Assert.False(CodeDeployTools.ContainsInstance(response, ""));
        Assert.False(CodeDeployTools.ContainsInstance(response, "i-ABCDEF"));
        Assert.False(CodeDeployTools.ContainsInstance(null, "i-abcdef"));
    }

    [Fact]
    public void Invalid_storage_class_is_rejected_not_defaulted()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            OssTools.BuildCreateBucketRequest("b", "StandardX", "LRS"));
        Assert.ThrowsAny<ArgumentException>(() =>
            OssTools.BuildCreateBucketRequest("b", "Standard", "lrs"));

        var request = OssTools.BuildCreateBucketRequest("b", "IA", "ZRS");
        Assert.Equal(Aliyun.OSS.StorageClass.IA, request.StorageClass);
        Assert.Equal(Aliyun.OSS.DataRedundancyType.ZRS, request.DataRedundancyType);
    }

    [Theory]
    [InlineData("""echo "a\nb" """, new[] { "echo", @"a\nb" })]
    [InlineData("""echo 'a\b' """, new[] { "echo", @"a\b" })]
    [InlineData("""echo a\"b """, new[] { "echo", "a\"b" })]
    public void Tokenizer_follows_posix_shlex(string script, string[] expected)
    {
        Assert.Equal(expected, LocalTools.SplitArguments(script));
    }

    [Fact]
    public void Tokenizer_rejects_trailing_backslash_and_unclosed_quote()
    {
        Assert.Throws<ArgumentException>(() => LocalTools.SplitArguments("""echo \"""));
        Assert.Throws<ArgumentException>(() => LocalTools.SplitArguments("""echo "abc"""));
    }

    [Fact]
    public void Golang_matched_binary_has_no_true_suffix()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["app/server"], "app");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "golang", analysis, "app", "app", 8080);

        Assert.Contains("nohup ./app/server", command);
        Assert.DoesNotContain("|| true", command);
    }

    [Fact]
    public void Golang_fallback_binary_keeps_true_suffix()
    {
        var analysis = new DeployFileAnalysis("file", null, "myapp", [], null);

        var command = DeployRules.GenerateStartCommand("/tmp/myapp", "golang", analysis, null, "app", 8080);

        Assert.EndsWith("|| true", command);
    }

    [Fact]
    public void Missing_required_argument_is_invalid_parameter()
    {
        var schema = new JsonObject { ["required"] = new JsonArray("RegionId") };

        var exception = Assert.Throws<OpsException>(() =>
            DynamicApiToolFactory.RequireArguments(schema, new Dictionary<string, JsonElement>()));

        Assert.Equal("InvalidParameter", exception.Code);
        Assert.Equal("RegionId is required and cannot be empty.", exception.Message);
    }

    [Fact]
    public void Dynamic_schema_matches_pydantic_shape()
    {
        var meta = new ApiMeta("s", "/", ["post"], "RPC",
        [
            new ApiParameter("InstanceIds", "query", JsonDocument.Parse("""{"type":"string","required":true,"description":"ID"}""").RootElement),
            new ApiParameter("Opt", "query", JsonDocument.Parse("""{"type":"object","description":"o"}""").RootElement),
            new ApiParameter("Nested.Value", "query", JsonDocument.Parse("""{"type":"string"}""").RootElement),
        ]);

        var schema = DynamicApiToolFactory.BuildInputSchema("ecs", meta);
        var properties = schema["properties"]!.AsObject();

        Assert.Equal("array", properties["InstanceIds"]!["type"]!.GetValue<string>());
        Assert.Equal("Instanceids", properties["InstanceIds"]!["title"]!.GetValue<string>());
        Assert.Contains("参数类型: string", properties["InstanceIds"]!["description"]!.GetValue<string>());
        Assert.Empty(properties["InstanceIds"]!["items"]!.AsObject());
        Assert.True(properties["Opt"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Null(properties["Opt"]!["default"]);
        Assert.False(properties.ContainsKey("Nested.Value"));
        Assert.Equal("cn-hangzhou", properties["RegionId"]!["default"]!.GetValue<string>());
        Assert.Equal(["InstanceIds"], schema["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["properties", "required", "type"], schema.AsObject().Select(x => x.Key));
    }

    [Fact]
    public void Dynamic_schema_omits_empty_required()
    {
        var meta = new ApiMeta("s", "/", ["post"], "RPC", []);

        var schema = DynamicApiToolFactory.BuildInputSchema("ecs", meta);

        Assert.False(schema.AsObject().ContainsKey("required"));
    }

    [Fact]
    public void Instructions_match_upstream_layout()
    {
        Assert.EndsWith("\n", ServerInstructions.Text);
        Assert.StartsWith("智能运维场景：", ServerInstructions.Text);
        Assert.DoesNotContain("\n            智能", ServerInstructions.Text);
        Assert.Equal(2099, ServerInstructions.Text.Length);
    }

    [Fact]
    public async Task Empty_executions_fail_immediately()
    {
        var caller = new ScriptedCaller(
            JsonNode.Parse("""{"body":{"Execution":{"ExecutionId":"exec-1"}}}"""),
            JsonNode.Parse("""{"body":{"Executions":[]}}"""));
        var executor = new OosExecutor(caller, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<OpsException>(() => executor.StartExecutionAsync(
            "cn-hangzhou", "ACS-ECS-BulkyRunCommand", [], TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal("Execution.Failed", exception.Code);
        Assert.Equal("OOS execution exec-1 returned no execution detail.", exception.Message);
    }

    [Fact]
    public async Task Failed_execution_reports_status_message()
    {
        var caller = new ScriptedCaller(
            JsonNode.Parse("""{"body":{"Execution":{"ExecutionId":"exec-2"}}}"""),
            JsonNode.Parse("""{"body":{"Executions":[{"Status":"Failed","StatusMessage":"disk full"}]}}"""));
        var executor = new OosExecutor(caller, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<OpsException>(() => executor.StartExecutionAsync(
            "cn-hangzhou", "t", [], TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal("Execution.Failed", exception.Code);
        Assert.Equal("OOS Execution Failed, reason: disk full.", exception.Message);
    }

    [Fact]
    public async Task Success_returns_execution_body()
    {
        var caller = new ScriptedCaller(
            JsonNode.Parse("""{"body":{"Execution":{"ExecutionId":"exec-3"}}}"""),
            JsonNode.Parse("""{"statusCode":200,"body":{"Executions":[{"Status":"Success"}],"RequestId":"req-3"}}"""));
        var executor = new OosExecutor(caller, TimeProvider.System);

        var body = await executor.StartExecutionAsync("cn-hangzhou", "t", [], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("req-3", body!["RequestId"]!.GetValue<string>());
        Assert.Null(body["statusCode"]);
    }

    [Fact]
    public async Task Non_terminal_execution_times_out()
    {
        var caller = new ScriptedCaller(
            JsonNode.Parse("""{"body":{"Execution":{"ExecutionId":"exec-4"}}}"""),
            JsonNode.Parse("""{"body":{"Executions":[{"Status":"Running"}]}}"""));
        var executor = new OosExecutor(caller, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<OpsException>(() => executor.StartExecutionAsync(
            "cn-hangzhou", "t", [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal("Timeout", exception.Code);
        Assert.Contains("did not reach a terminal state in 1 seconds", exception.Message);
    }

    [Fact]
    public void Deployment_info_is_merged_and_write_failure_is_swallowed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deploy-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".code_deploy"));
        var file = Path.Combine(root, ".code_deploy", "application.json");
        File.WriteAllText(file, """{"other_key":"keep"}""");
        var tools = new CodeDeployTools(null!, null!, NullLogger<CodeDeployTools>.Instance);

        try
        {
            tools.SaveApplicationInfo(root, "app", "group", "cn-hangzhou", 8080, ["i-1"]);

            var saved = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Equal("keep", saved["other_key"]!.GetValue<string>());
            Assert.Equal("app", saved["last_deployment"]!["application_name"]!.GetValue<string>());

            File.WriteAllText(file, "not json");
            tools.SaveApplicationInfo(root, "app2", "group", "cn-hangzhou", 8080, ["i-1"]);
            Assert.Equal("app2", JsonNode.Parse(File.ReadAllText(file))!["last_deployment"]!["application_name"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Python_version_only_read_with_python_project_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".python-version"), "3.12\n");

            var bare = DeployAnalyzer.Analyze(new DirectoryInfo(root));
            Assert.Null(bare["runtime_versions"]!["python"]);

            File.WriteAllText(Path.Combine(root, "requirements.txt"), "requests\n");
            var withMarker = DeployAnalyzer.Analyze(new DirectoryInfo(root));
            Assert.Equal("3.12", withMarker["runtime_versions"]!["python"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Text(CallToolResult result) =>
        result.Content[0] is TextContentBlock text ? text.Text : string.Empty;

    private sealed class ScriptedCaller : GenericApiCaller
    {
        private readonly Queue<JsonNode?> _responses;
        private JsonNode? _last;

        public ScriptedCaller(params JsonNode?[] responses)
            : base(null!, null!, null!, TimeProvider.System, NullLogger<GenericApiCaller>.Instance)
        {
            _responses = new Queue<JsonNode?>(responses);
        }

        public override Task<JsonNode?> CallAsync(
            string service, string api, IReadOnlyDictionary<string, JsonElement> parameters,
            string? endpointOverride, CancellationToken cancellationToken = default)
        {
            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            return Task.FromResult(_last);
        }
    }
}