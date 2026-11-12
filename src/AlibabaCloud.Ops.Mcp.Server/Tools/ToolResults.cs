using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>统一的工具返回构造与响应字段查找。</summary>
internal static class ToolResults
{
    /// <summary>把 JSON 节点包装为成功结果。</summary>
    public static CallToolResult Text(JsonNode? node) => new()
    {
        Content = [new TextContentBlock { Text = node?.ToJsonString() ?? "null" }],
        IsError = false,
    };

    /// <summary>把纯文本包装为成功结果。</summary>
    public static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = false,
    };

    /// <summary>构造 isError 结果。</summary>
    public static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };

    /// <summary>用异常消息构造错误结果。</summary>
    public static CallToolResult Error(Exception exception) => Error(exception.Message);

    /// <summary>在任意层级按名称（忽略大小写）查找字段。</summary>
    public static JsonNode? Find(JsonNode? node, string propertyName)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }

                    var nested = Find(value, propertyName);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    var nested = Find(item, propertyName);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }
}
