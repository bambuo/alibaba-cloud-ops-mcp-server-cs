using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Tools;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>启动 OOS 执行并轮询到终态，带总超时与取消。</summary>
public sealed class OosExecutor(
    GenericApiCaller caller,
    TimeProvider timeProvider,
    ILogger<OosExecutor> logger)
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
            logger.LogWarning("StartExecution returned no ExecutionId for template {Template}", templateName);
            return Body(startResponse);
        }

        var deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);

            var pollArguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
                ["ExecutionId"] = JsonSerializer.SerializeToElement(executionId),
            };

            var pollResponse = await caller.CallAsync("oos", "ListExecutions", pollArguments, cancellationToken);
            var status = ToolResults.Find(pollResponse, "Status")?.GetValue<string>();
            if (status is not null && EndStatuses.Contains(status))
            {
                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var message = ToolResults.Find(pollResponse, "StatusMessage")?.GetValue<string>() ?? "Execution failed";
                    throw new CloudApiException("OOSExecutionFailed", message);
                }

                return Body(pollResponse);
            }
        }

        throw new CloudApiException("ExecutionTimeout", $"Execution {executionId} did not finish within {timeout.TotalSeconds:0} seconds");
    }

    /// <summary>对齐上游：工具返回 ListExecutions 的 body，而不是完整响应包裹。</summary>
    internal static JsonNode? Body(JsonNode? response) => ToolResults.Find(response, "body") ?? response;
}
