namespace AlibabaCloud.Ops.Mcp.Server.Options;

/// <summary>支持的传输方式。</summary>
public enum TransportKind
{
    Stdio,
    StreamableHttp,
}

/// <summary>阿里云部署环境，决定 bssopenapi 等服务的端点。</summary>
public enum CloudEnvironment
{
    Domestic,
    International,
}

/// <summary>命令行参数错误，进程以退出码 2 结束。</summary>
public sealed class OptionException(string message) : Exception(message);

/// <summary>启动参数集合，负责解析 CLI 参数并归一化服务码与工具名。</summary>
public sealed class ServerOptions
{
    /// <summary>传输方式，默认 stdio。</summary>
    public TransportKind Transport { get; init; } = TransportKind.Stdio;
    /// <summary>HTTP 监听端口，默认 8000。</summary>
    public int Port { get; init; } = 8000;
    /// <summary>HTTP 监听地址，默认仅回环。</summary>
    public string Host { get; init; } = "127.0.0.1";
    /// <summary>`--services` 原始值，逗号分隔的服务码。</summary>
    public string? Services { get; init; }
    /// <summary>为 true 时只接受请求头凭证，不向环境变量与默认链回退。</summary>
    public bool HeadersCredentialOnly { get; init; }
    /// <summary>阿里云环境，默认国内。</summary>
    public CloudEnvironment Environment { get; init; } = CloudEnvironment.Domestic;
    /// <summary>为 true 时只进入代码部署模式。</summary>
    public bool CodeDeploy { get; init; }
    /// <summary>`--extra-config` 的原始 JSON。</summary>
    public string? ExtraConfig { get; init; }
    /// <summary>`--visible-tools` 原始值，逗号分隔的工具名。</summary>
    public string? VisibleTools { get; init; }

    /// <summary>去重、转小写后的服务码列表。</summary>
    public IReadOnlyList<string> ServiceCodes =>
        string.IsNullOrWhiteSpace(Services)
            ? []
            : Services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant())
                .Distinct()
                .ToList();

    /// <summary>按顺序保留原始大小写的工具白名单。</summary>
    public IReadOnlyList<string> VisibleToolNames =>
        string.IsNullOrWhiteSpace(VisibleTools)
            ? []
            : VisibleTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    /// <summary>解析 CLI 参数；未知参数、非法取值与不支持的传输均抛 <see cref="OptionException"/>。</summary>
    public static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions();
        var transport = "stdio";
        var environment = "domestic";
        var port = 8000;
        var host = "127.0.0.1";
        string? services = null;
        string? extraConfig = null;
        string? visibleTools = null;
        var headersOnly = false;
        var codeDeploy = false;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (!raw.StartsWith("--", StringComparison.Ordinal))
            {
                throw new OptionException($"Unexpected argument: {raw}");
            }

            var (name, inlineValue) = SplitArgument(raw);

            // 读取 --name value 或 --name=value 形式的取值。
            string TakeValue()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new OptionException($"Option {name} requires a value");
                }

                return args[++i];
            }

            // 开关参数：无内联值时视为 true，支持 --flag=false 关闭。
            bool TakeBool()
            {
                if (inlineValue is null)
                {
                    return true;
                }

                return inlineValue.ToLowerInvariant() switch
                {
                    "true" or "1" => true,
                    "false" or "0" => false,
                    _ => throw new OptionException($"Option {name} expects true or false"),
                };
            }

            switch (name)
            {
                case "--transport":
                    transport = TakeValue();
                    break;
                case "--port":
                    if (!int.TryParse(TakeValue(), out port) || port is < 1 or > 65535)
                    {
                        throw new OptionException("Option --port expects an integer between 1 and 65535");
                    }

                    break;
                case "--host":
                    host = TakeValue();
                    break;
                case "--services":
                    services = TakeValue();
                    break;
                case "--headers-credential-only":
                    headersOnly = TakeBool();
                    break;
                case "--env":
                    environment = TakeValue();
                    break;
                case "--code-deploy":
                    codeDeploy = TakeBool();
                    break;
                case "--extra-config":
                    extraConfig = TakeValue();
                    break;
                case "--visible-tools":
                    visibleTools = TakeValue();
                    break;
                default:
                    throw new OptionException($"Unknown option: {name}");
            }
        }

        var transportKind = transport.ToLowerInvariant() switch
        {
            "stdio" => TransportKind.Stdio,
            "streamable-http" => TransportKind.StreamableHttp,
            "sse" => throw new OptionException("UnsupportedTransport: sse is not supported"),
            _ => throw new OptionException("Option --transport expects stdio or streamable-http"),
        };

        var environmentKind = environment.ToLowerInvariant() switch
        {
            "domestic" => CloudEnvironment.Domestic,
            "international" => CloudEnvironment.International,
            _ => throw new OptionException("Option --env expects domestic or international"),
        };

        if (extraConfig is not null)
        {
            ExtraConfigParser.Validate(extraConfig);
        }

        return new ServerOptions
        {
            Transport = transportKind,
            Port = port,
            Host = host,
            Services = services,
            HeadersCredentialOnly = headersOnly,
            Environment = environmentKind,
            CodeDeploy = codeDeploy,
            ExtraConfig = extraConfig,
            VisibleTools = visibleTools,
        };
    }

    /// <summary>按第一个等号拆出参数名与内联值。</summary>
    private static (string Name, string? Value) SplitArgument(string raw)
    {
        var index = raw.IndexOf('=');
        return index < 0
            ? (raw, null)
            : (raw[..index], raw[(index + 1)..]);
    }
}
