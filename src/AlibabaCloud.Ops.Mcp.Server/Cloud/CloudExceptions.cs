namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>带对外错误码的工具失败，消息中不含错误码前缀。</summary>
public class OpsException(string code, string message, string? requestId = null) : Exception(message)
{
    /// <summary>对外错误码，与 Java 侧错误码表一致。</summary>
    public string Code { get; } = code;

    /// <summary>阿里云 RequestId，缺失时不写入错误载荷。</summary>
    public string? RequestId { get; } = requestId;
}

/// <summary>未找到任何可用凭证。</summary>
public sealed class MissingCredentialsException(string message) : OpsException("CredentialMissing", message);

/// <summary>云调用或元数据错误，Code 为对外的错误标识。</summary>
public sealed class CloudApiException(string code, string message, string? requestId = null)
    : OpsException(code, message, requestId);

/// <summary>把 SDK 异常归一为带错误码与 RequestId 的失败。</summary>
public static class CloudErrors
{
    // SDK 异常类型与属性随版本变化，这里按属性名取值而不是绑定具体类型。
    /// <summary>读取 SDK 异常的 Code/RequestId，取不到时回退为 InternalError。</summary>
    public static OpsException FromSdk(Exception exception)
    {
        if (exception is OpsException ops)
        {
            return ops;
        }

        var type = exception.GetType();
        var code = ReadString(type, exception, "Code");
        var requestId = ReadString(type, exception, "RequestId");
        var message = ReadString(type, exception, "Message") ?? exception.Message;
        return new OpsException(string.IsNullOrWhiteSpace(code) ? "InternalError" : code, message, requestId);
    }

    private static string? ReadString(Type type, object instance, string property)
    {
        var value = type.GetProperty(property)?.GetValue(instance);
        return value switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            _ => value.ToString(),
        };
    }
}