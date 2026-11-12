using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class CredentialProviderTests
{
    [Fact]
    public void Prefers_request_headers()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-acs-accesskey-id"] = "header-id";
        context.Request.Headers["x-acs-accesskey-secret"] = "header-secret";
        context.Request.Headers["x-acs-security-token"] = "header-token";

        var provider = Create(ServerOptions.Parse([]), context);
        var credentials = provider.Resolve();

        Assert.Equal("headers", credentials.Source);
        Assert.Equal("header-id", credentials.AccessKeyId);
        Assert.Equal("header-token", credentials.SecurityToken);
    }

    [Fact]
    public void Falls_back_to_environment()
    {
        var previousId = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID");
        var previousSecret = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", "env-id");
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", "env-secret");

            var credentials = Create(ServerOptions.Parse([]), new DefaultHttpContext()).Resolve();

            Assert.Equal("environment", credentials.Source);
            Assert.Equal("env-id", credentials.AccessKeyId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", previousId);
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", previousSecret);
        }
    }

    [Fact]
    public void Headers_only_mode_does_not_fall_back()
    {
        var provider = Create(ServerOptions.Parse(["--headers-credential-only"]), new DefaultHttpContext());

        var exception = Assert.Throws<MissingCredentialsException>(() => provider.Resolve());
        Assert.Contains("headers-credential-only", exception.Message);
    }

    [Fact]
    public void Ignores_incomplete_header_credentials()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-acs-accesskey-id"] = "only-id";

        var provider = Create(ServerOptions.Parse(["--headers-credential-only"]), context);

        Assert.Throws<MissingCredentialsException>(() => provider.Resolve());
    }

    [Fact]
    public void Falls_back_to_default_chain()
    {
        var previousId = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID");
        var previousSecret = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", null);
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", null);

            var provider = Create(ServerOptions.Parse([]), new DefaultHttpContext(),
                new FakeChain(new CloudCredentials("chain-id", "chain-secret", null, "credential-chain")));
            var credentials = provider.Resolve();

            Assert.Equal("credential-chain", credentials.Source);
            Assert.Equal("chain-id", credentials.AccessKeyId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", previousId);
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", previousSecret);
        }
    }

    [Fact]
    public void Throws_when_no_credential_is_available()
    {
        var previousId = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID");
        var previousSecret = Environment.GetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", null);
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", null);

            var provider = Create(ServerOptions.Parse([]), new DefaultHttpContext());
            Assert.Throws<MissingCredentialsException>(() => provider.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_ID", previousId);
            Environment.SetEnvironmentVariable("ALIBABA_CLOUD_ACCESS_KEY_SECRET", previousSecret);
        }
    }

    private sealed class FakeChain(CloudCredentials? credentials) : IDefaultCredentialChain
    {
        public CloudCredentials? Resolve() => credentials;
    }

    private static CredentialProvider Create(
        ServerOptions options,
        HttpContext? context,
        IDefaultCredentialChain? chain = null) =>
        new(options, new HttpContextAccessor { HttpContext = context },
            chain ?? new FakeChain(null), NullLogger<CredentialProvider>.Instance);
}
