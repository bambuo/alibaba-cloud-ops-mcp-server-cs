using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Tools;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class LocalToolsTests
{
    private readonly LocalTools _tools = new();

    [Fact]
    public async Task Lists_directory_entries()
    {
        var directory = Directory.CreateTempSubdirectory("ops-mcp-list-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "app.txt"), "hello");

            var result = await _tools.ListDirectory(directory.FullName);
            var payload = JsonNode.Parse(Text(result))!.AsObject();
            var items = payload["items"]!.AsArray();

            Assert.False(result.IsError ?? false);
            Assert.Equal(directory.FullName, payload["path"]!.GetValue<string>());
            Assert.Equal(items.Count, payload["count"]!.GetValue<int>());
            var entry = items.Single(x => x!["name"]!.GetValue<string>() == "app.txt");
            Assert.Equal("file", entry!["type"]!.GetValue<string>());
            Assert.Equal(5, entry["size"]!.GetValue<long>());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Returns_error_for_missing_directory()
    {
        var result = await _tools.ListDirectory("/definitely/not/exists");

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameter", Text(result));
        Assert.Contains("Failed to list directory", Text(result));
    }

    [Fact]
    public async Task Runs_shell_command_and_captures_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await _tools.RunShellScript("echo hello", timeout: 10);
        var payload = JsonNode.Parse(Text(result))!;

        Assert.False(result.IsError ?? false);
        Assert.Equal(0, payload["exit_code"]!.GetValue<int>());
        Assert.Contains("hello", payload["stdout"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runs_direct_command_without_shell_when_requested()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await _tools.RunShellScript("/bin/echo \"a b\"", timeout: 10, shell: false);
        var payload = JsonNode.Parse(Text(result))!;

        Assert.False(result.IsError ?? false);
        Assert.Equal("a b", payload["stdout"]!.GetValue<string>().Trim());
    }

    [Theory]
    [InlineData("echo hello", new[] { "echo", "hello" })]
    [InlineData("/bin/echo 'a b'", new[] { "/bin/echo", "a b" })]
    [InlineData("/bin/echo a\\ b", new[] { "/bin/echo", "a b" })]
    public void Splits_command_line_like_posix_shell(string commandLine, string[] expected)
    {
        Assert.Equal(expected, LocalTools.SplitArguments(commandLine));
    }

    [Fact]
    public async Task Times_out_long_running_command()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await _tools.RunShellScript("sleep 5", timeout: 1);

        Assert.True(result.IsError);
        Assert.Contains("\"Code\":\"Timeout\"", Text(result));
        Assert.Contains("Command execution timeout (exceeded 1 seconds)", Text(result));
    }

    [Fact]
    public async Task Analyzes_dotnet_project_stack()
    {
        var directory = Directory.CreateTempSubdirectory("ops-mcp-stack-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "package.json"),
                "{\"engines\":{\"node\":\">=20\"}}");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "go.mod"), "module demo\n\ngo 1.23\n");

            var result = await _tools.AnalyzeDeployStack(directory.FullName);
            var payload = JsonNode.Parse(Text(result))!.AsObject();

            Assert.True(payload["detected"]!.GetValue<bool>());
            var methods = payload["deployment_methods"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert.Contains("npm", methods);
            Assert.Contains("go", methods);
            Assert.Equal(">=20", payload["runtime_versions"]!["node"]!.GetValue<string>());
            Assert.Equal("1.23", payload["runtime_versions"]!["go"]!.GetValue<string>());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content[0] is ModelContextProtocol.Protocol.TextContentBlock text ? text.Text : string.Empty;
}
