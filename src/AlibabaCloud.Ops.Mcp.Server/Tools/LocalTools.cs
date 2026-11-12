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
            var directory = ValidatePath(path);
            if (!directory.Exists)
            {
                throw new ArgumentException($"Path is not a directory: {path}");
            }

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
            return Task.FromResult(ToolResults.Error("InvalidParameter", $"Failed to list directory: {exception.Message}"));
        }
    }
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
                return ToolResults.Error("InvalidParameter", "timeout must be between 1 and 86400 seconds");
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
                List<string> parts;
                try
                {
                    parts = SplitArguments(script);
                    if (parts.Count == 0)
                    {
                        // 与上游 subprocess.run([]) 的异常文本一致
                        throw new ArgumentException("list index out of range");
                    }
                }
                catch (Exception exception)
                {
                    return ToolResults.Error("LocalExecutionFailed", $"Failed to execute command: {exception.Message}");
                }

                startInfo.FileName = parts[0];
                for (var index = 1; index < parts.Count; index++)
                {
                    startInfo.ArgumentList.Add(parts[index]);
                }
            }

            if (!string.IsNullOrWhiteSpace(working_directory))
            {
                DirectoryInfo directory;
                try
                {
                    directory = ValidatePath(working_directory);
                }
                catch (Exception exception)
                {
                    return ToolResults.Error("LocalExecutionFailed", $"Failed to execute command: {exception.Message}");
                }

                if (!directory.Exists)
                {
                    return ToolResults.Error("LocalExecutionFailed",
                        $"Failed to execute command: Working directory is not a directory: {working_directory}");
                }

                startInfo.WorkingDirectory = directory.FullName;
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception exception)
            {
                var notFound = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }
                               || exception.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
                return ToolResults.Error("LocalExecutionFailed", notFound
                    ? $"Command or script not found: {script}"
                    : $"Failed to execute command: {exception.Message}");
            }

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

                return ToolResults.Error("Timeout", $"Command execution timeout (exceeded {timeout} seconds)");
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
            var root = ValidatePath(directory);
            if (!root.Exists)
            {
                throw new ArgumentException($"路径不是目录: {directory}");
            }

            var result = DeployAnalyzer.Analyze(root);
            return Task.FromResult(ToolResults.Text(result));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error("InvalidParameter",
                $"Failed to identify deployment method: {exception.Message}"));
        }
    }

    /// <summary>按 POSIX shlex.split 规则拆分命令：单引号内不转义，双引号内仅 \" 与 \\ 转义。</summary>
    internal static List<string> SplitArguments(string script)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        var index = 0;

        while (index < script.Length)
        {
            var character = script[index];
            if (character is ' ' or '\t' or '\r' or '\n')
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                index++;
            }
            else if (character == '\'')
            {
                inToken = true;
                var end = script.IndexOf('\'', index + 1);
                if (end < 0)
                {
                    throw new ArgumentException("No closing quotation");
                }

                current.Append(script, index + 1, end - index - 1);
                index = end + 1;
            }
            else if (character == '"')
            {
                inToken = true;
                index++;
                var closed = false;
                while (index < script.Length)
                {
                    var inner = script[index];
                    if (inner == '"')
                    {
                        closed = true;
                        index++;
                        break;
                    }

                    if (inner == '\\' && index + 1 < script.Length)
                    {
                        var escaped = script[index + 1];
                        if (escaped is '"' or '\\')
                        {
                            current.Append(escaped);
                            index += 2;
                        }
                        else
                        {
                            current.Append('\\');
                            index++;
                        }
                    }
                    else
                    {
                        current.Append(inner);
                        index++;
                    }
                }

                if (!closed)
                {
                    throw new ArgumentException("No closing quotation");
                }
            }
            else if (character == '\\')
            {
                if (index + 1 >= script.Length)
                {
                    throw new ArgumentException("No escaped character");
                }

                inToken = true;
                current.Append(script[index + 1]);
                index += 2;
            }
            else
            {
                inToken = true;
                current.Append(character);
                index++;
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>解析为规范化绝对路径；不存在或非法时统一报 Invalid path（与上游一致）。</summary>
    private static DirectoryInfo ValidatePath(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var directory = new DirectoryInfo(resolved);
            if (!directory.Exists && !File.Exists(resolved))
            {
                throw new ArgumentException($"Path does not exist: {path}");
            }

            return directory;
        }
        catch (Exception)
        {
            throw new ArgumentException($"Invalid path: {path}");
        }
    }
}
