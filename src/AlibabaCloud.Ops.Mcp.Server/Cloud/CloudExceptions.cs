namespace AlibabaCloud.Ops.Mcp.Server.Cloud;

/// <summary>未找到任何可用凭证。</summary>
public sealed class MissingCredentialsException(string message) : Exception(message);

/// <summary>云调用或元数据错误，Code 为对外的错误标识。</summary>
public sealed class CloudApiException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
