using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>本地目录、命令执行与部署栈识别工具。</summary>
[McpServerToolType]
public sealed class LocalTools
{
    /// <summary>列出目录内容，可选递归。</summary>
    [McpServerTool(Name = "LOCAL_ListDirectory")]
    [Description("列出指定目录中的文件和子目录。返回包含名称、类型和大小等信息的目录和文件列表。")]
    public Task<CallToolResult> ListDirectory(
        [Description("Directory path to list (absolute or relative path)")] string path,
        [Description("Whether to recursively list subdirectories")] bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = ValidateDirectory(path);
            var items = new List<JsonObject>();

            var entries = recursive
                ? directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                : directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly);

            foreach (var entry in entries)
            {
                try
                {
                    items.Add(new JsonObject
                    {
                        ["name"] = entry.Name,
                        ["path"] = entry.FullName,
                        ["type"] = entry is DirectoryInfo ? "directory" : "file",
                        ["size"] = entry is FileInfo file ? file.Length : null,
                        ["modified"] = new DateTimeOffset(entry.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000d,
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            var ordered = items.OrderBy(item => item["name"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
            var results = new JsonArray(ordered.Select(item => (JsonNode?)item).ToArray());
            return Task.FromResult(ToolResults.Text(new JsonObject
            {
                ["path"] = directory.FullName,
                ["items"] = results,
                ["count"] = ordered.Count,
            }));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error(exception));
        }
    }

    /// <summary>执行 shell 命令并返回输出、退出码与超时状态。</summary>
    /// <summary>执行 shell 命令并返回输出与退出码。</summary>
    [McpServerTool(Name = "LOCAL_RunShellScript")]
    [Description("执行 shell 脚本或命令。返回命令输出、错误信息和退出代码。注意：执行任意命令可能存在安全风险，请谨慎使用。")]
    public async Task<CallToolResult> RunShellScript(
        [Description("Shell script content or command to execute")] string script,
        [Description("Working directory for command execution")] string? working_directory = null,
        [Description("Command execution timeout in seconds")] int timeout = 300,
        [Description("Whether to use shell execution (True uses /bin/sh, False executes command directly)")] bool shell = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (timeout is <= 0 or > 86400)
            {
                return ToolResults.Error("InvalidParameter: timeout must be between 1 and 86400 seconds");
            }

            var startInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (shell)
            {
                if (OperatingSystem.IsWindows())
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.ArgumentList.Add("/c");
                    startInfo.ArgumentList.Add(script);
                }
                else
                {
                    startInfo.FileName = "/bin/sh";
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add(script);
                }
            }
            else
            {
                var parts = SplitArguments(script);
                if (parts.Count == 0)
                {
                    return ToolResults.Error("InvalidParameter: script is empty");
                }

                startInfo.FileName = parts[0];
                for (var index = 1; index < parts.Count; index++)
                {
                    startInfo.ArgumentList.Add(parts[index]);
                }
            }

            if (!string.IsNullOrWhiteSpace(working_directory))
            {
                var directory = ValidateDirectory(working_directory);
                startInfo.WorkingDirectory = directory.FullName;
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return ToolResults.Error($"CommandTimeout: exceeded {timeout} seconds");
            }

            var result = new JsonObject
            {
                ["command"] = script,
                ["exit_code"] = process.ExitCode,
                ["stdout"] = await standardOutput,
                ["stderr"] = await standardError,
                ["success"] = process.ExitCode == 0,
            };

            return ToolResults.Text(result);
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>识别项目的部署方式与技术栈。</summary>
    [McpServerTool(Name = "LOCAL_AnalyzeDeployStack")]
    [Description("识别项目部署方式和技术栈。支持识别 npm、Python、Java、Go、Docker 等部署方式。")]
    public Task<CallToolResult> AnalyzeDeployStack(
        [Description("Directory path to analyze (absolute or relative path)")] string directory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = ValidateDirectory(directory);
            var result = DeployAnalyzer.Analyze(root);
            return Task.FromResult(ToolResults.Text(result));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error(exception));
        }
    }

    /// <summary>按 POSIX 规则拆分命令行：支持单双引号包裹与反斜杠转义。</summary>
    internal static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var hasToken = false;
        var quote = '\0';

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else if (character == '\\' && quote == '"' && index + 1 < commandLine.Length)
                {
                    current.Append(commandLine[++index]);
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (hasToken)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else if (character == '\\' && index + 1 < commandLine.Length)
            {
                current.Append(commandLine[++index]);
                hasToken = true;
            }
            else
            {
                current.Append(character);
                hasToken = true;
            }
        }

        if (quote != '\0')
        {
            throw new ArgumentException("InvalidParameter: unbalanced quotes");
        }

        if (hasToken)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>校验路径存在且为目录。</summary>
    private static DirectoryInfo ValidateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("InvalidPath: path is required");
        }

        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"NotFound: {path}");
        }

        return directory;
    }
}
