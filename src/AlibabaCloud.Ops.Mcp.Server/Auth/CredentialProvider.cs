using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AlibabaCloud.Ops.Mcp.Server.Auth;

/// <summary>一次调用使用的访问凭证，Source 记录来源便于排障且不含密钥。</summary>
public sealed record CloudCredentials(string AccessKeyId, string AccessKeySecret, string? SecurityToken, string Source);

/// <summary>凭证提供者：按优先级解析出可用凭证。</summary>
public interface ICredentialProvider
{
    /// <summary>返回可用凭证，解析失败抛 <see cref="MissingCredentialsException"/>。</summary>
    CloudCredentials Resolve();
}

/// <summary>默认凭证链抽象，便于在无云环境时替换实现。</summary>
public interface IDefaultCredentialChain
{
    /// <summary>链上找到凭证则返回，否则返回 null。</summary>
    CloudCredentials? Resolve();
}

/// <summary>包装 `Aliyun.Credentials` 默认链：环境变量、配置文件、RAM 角色、实例元数据。</summary>
public sealed class AliyunDefaultCredentialChain(ILogger<AliyunDefaultCredentialChain> logger) : IDefaultCredentialChain
{
    /// <summary>读取默认链凭证；不可用时降级为 null 而不是启动失败。</summary>
    public CloudCredentials? Resolve()
    {
        try
        {
            var credential = new Aliyun.Credentials.Client().GetCredential();
            if (string.IsNullOrWhiteSpace(credential.AccessKeyId) || string.IsNullOrWhiteSpace(credential.AccessKeySecret))
            {
                return null;
            }

            return new CloudCredentials(
                credential.AccessKeyId,
                credential.AccessKeySecret,
                string.IsNullOrWhiteSpace(credential.SecurityToken) ? null : credential.SecurityToken,
                "credential-chain");
        }
        catch (Exception exception)
        {
            logger.LogDebug("Default credential chain unavailable: {Message}", exception.Message);
            return null;
        }
    }
}

/// <summary>按「请求头 → 环境变量 → 默认链」解析凭证；headers-credential-only 模式不回退。</summary>
public sealed class CredentialProvider(
    ServerOptions options,
    IHttpContextAccessor httpContextAccessor,
    IDefaultCredentialChain defaultChain,
    ILogger<CredentialProvider> logger) : ICredentialProvider
{
    private const string HeaderAccessKeyId = "x-acs-accesskey-id";
    private const string HeaderAccessKeySecret = "x-acs-accesskey-secret";
    private const string HeaderSecurityToken = "x-acs-security-token";

    /// <summary>按优先级解析凭证；全部失败时抛 <see cref="MissingCredentialsException"/>。</summary>
    public CloudCredentials Resolve()
    {
        var fromHeaders = ResolveFromHeaders();
        if (fromHeaders is not null)
        {
            logger.LogDebug("Credentials resolved from request headers");
            return fromHeaders;
        }

        if (options.HeadersCredentialOnly)
        {
            throw new MissingCredentialsException("MissingCredentials: headers-credential-only is enabled");
        }

        var fromEnvironment = ResolveFromEnvironment();
        if (fromEnvironment is not null)
        {
            logger.LogDebug("Credentials resolved from environment variables");
            return fromEnvironment;
        }

        var fromChain = defaultChain.Resolve();
        if (fromChain is not null)
        {
            logger.LogDebug("Credentials resolved from the default credential chain");
            return fromChain;
        }

        throw new MissingCredentialsException("MissingCredentials: no AccessKey found in headers or environment");
    }

    /// <summary>读取请求头凭证，缺少 id 或 secret 时视为未提供。</summary>
    private CloudCredentials? ResolveFromHeaders()
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return null;
        }

        var id = headers[HeaderAccessKeyId].ToString();
        var secret = headers[HeaderAccessKeySecret].ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var token = headers[HeaderSecurityToken].ToString();
        return new CloudCredentials(id, secret, string.IsNullOrWhiteSpace(token) ? null : token, "headers");
    }

    /// <summary>读取 ALIBABA_CLOUD_* 环境变量凭证。</summary>
    private static CloudCredentials? ResolveFromEnvironment()
    {
        var id = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_SECURITY_TOKEN");
        return new CloudCredentials(id, secret, string.IsNullOrWhiteSpace(token) ? null : token, "environment");
    }


}
