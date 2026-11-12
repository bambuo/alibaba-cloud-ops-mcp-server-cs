using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>启动 OOS 执行并轮询到终态，带总超时与取消。</summary>
public sealed class OosExecutor(
    GenericApiCaller caller,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> EndStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Success", "Failed", "Cancelled" };

    /// <summary>启动模板执行；失败抛 OOSExecutionFailed，超时抛 ExecutionTimeout。</summary>
    public async Task<JsonNode?> StartExecutionAsync(
        string regionId,
        string templateName,
        JsonObject parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startArguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
            ["TemplateName"] = JsonSerializer.SerializeToElement(templateName),
            ["Parameters"] = JsonSerializer.SerializeToElement(parameters.ToJsonString()),
        };

        var startResponse = await caller.CallAsync("oos", "StartExecution", startArguments, cancellationToken);
        var executionId = ToolResults.Find(startResponse, "ExecutionId")?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new OpsException("InternalError", "StartExecution returned no ExecutionId.");
        }

        var deadline = timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var pollArguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
                ["ExecutionId"] = JsonSerializer.SerializeToElement(executionId),
            };

            var pollResponse = await caller.CallAsync("oos", "ListExecutions", pollArguments, cancellationToken);
            var body = Body(pollResponse);
            var executions = body?["Executions"] as JsonArray;
            if (executions is null || executions.Count == 0)
            {
                throw new OpsException("Execution.Failed",
                    $"OOS execution {executionId} returned no execution detail.");
            }

            var execution = executions[0];
            var status = execution?["Status"]?.GetValue<string>();
            if (string.Equals(status, "Failed", StringComparison.Ordinal))
            {
                var message = execution?["StatusMessage"]?.GetValue<string>();
                throw new OpsException("Execution.Failed", $"OOS Execution Failed, reason: {message}.");
            }

            if (status is not null && EndStatuses.Contains(status))
            {
                return body;
            }

            if (timeProvider.GetUtcNow() > deadline)
            {
                throw new OpsException("Timeout",
                    $"OOS execution {executionId} did not reach a terminal state in {timeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
    }

    /// <summary>对齐上游：工具返回 ListExecutions 的 body，而不是完整响应包裹。</summary>
    internal static JsonNode? Body(JsonNode? response) => ToolResults.Find(response, "body") ?? response;
}
