namespace AlibabaCloud.Ops.Mcp.Server.Options;

/// <summary>支持的传输方式。</summary>
public enum TransportKind
{
    Stdio,
    StreamableHttp,
    Both,
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
    /// <summary>帮助文本，与 Java 侧保持一致。</summary>
    public const string Usage = """
        Usage: alibaba-cloud-ops-mcp-server [OPTIONS]

          --transport <stdio|streamable-http|both>   Transport type (default: stdio)
          --port <int>                               HTTP port (default: 8000)
          --host <string>                            HTTP host (default: 127.0.0.1)
          --services <a,b>                           Extra common API services
          --headers-credential-only [true|false]     Only accept credentials from HTTP headers
          --env <domestic|international>             Endpoint environment (default: domestic)
          --code-deploy                              Register only deployment related tools
          --extra-config <json>                      Extra service to API list mapping
          --visible-tools <a,b>                      Tool whitelist (case-insensitive)
          --oos-timeout-seconds <int>                OOS execution poll timeout (default: 900)
          --meta-cache-ttl-seconds <int>             POP metadata cache TTL (default: 600)
          --enable-local-tools [true|false]          Register LOCAL_* tools (default: true)
          --help                                     Print this help
        """;

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
    /// <summary>`--extra-config` 的原始 JSON，解析失败时为 null。</summary>
    public string? ExtraConfig { get; init; }
    /// <summary>`--visible-tools` 原始值，逗号分隔的工具名。</summary>
    public string? VisibleTools { get; init; }
    /// <summary>OOS 执行轮询总超时秒数，默认 900。</summary>
    public int OosTimeoutSeconds { get; init; } = 900;
    /// <summary>POP 元数据缓存 TTL 秒数，默认 600。</summary>
    public int MetaCacheTtlSeconds { get; init; } = 600;
    /// <summary>为 false 时不注册 LOCAL_* 工具。</summary>
    public bool EnableLocalTools { get; init; } = true;
    /// <summary>为 true 时只打印帮助并退出。</summary>
    public bool Help { get; init; }

    /// <summary>按逗号拆分的服务码，保留原始大小写与顺序。</summary>
    public IReadOnlyList<string> ServiceCodes =>
        string.IsNullOrWhiteSpace(Services)
            ? []
            : Services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
        var transport = "stdio";
        var environment = "domestic";
        var port = 8000;
        var host = "127.0.0.1";
        string? services = null;
        string? extraConfig = null;
        string? visibleTools = null;
        var headersOnly = false;
        var codeDeploy = false;
        var oosTimeout = 900;
        var metaTtl = 600;
        var enableLocalTools = true;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (raw is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (!raw.StartsWith("--", StringComparison.Ordinal))
            {
                throw new OptionException($"Unknown option: {raw}");
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

            // 开关参数：无取值时视为 true，支持 --flag=false 与 --flag false 两种关闭写法。
            bool TakeBool()
            {
                var value = inlineValue;
                if (value is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                if (value is null)
                {
                    return true;
                }

                return value.ToLowerInvariant() switch
                {
                    "true" or "1" => true,
                    "false" or "0" => false,
                    _ => throw new OptionException($"Option {name} requires true or false"),
                };
            }

            int TakeInt()
            {
                var value = TakeValue();
                if (!int.TryParse(value, out var number))
                {
                    throw new OptionException($"Option {name} requires an integer");
                }

                return number;
            }

            switch (name)
            {
                case "--transport":
                    transport = TakeValue();
                    break;
                case "--port":
                    port = TakeInt();
                    if (port is < 1 or > 65535)
                    {
                        throw new OptionException($"Invalid --port: {port}");
                    }

                    break;
                case "--host":
                    host = TakeValue();
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        throw new OptionException($"Option {name} requires a value");
                    }

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
                    extraConfig = ExtraConfigParser.Validate(TakeValue());
                    break;
                case "--visible-tools":
                    visibleTools = TakeValue();
                    break;
                case "--oos-timeout-seconds":
                    oosTimeout = TakeInt();
                    break;
                case "--meta-cache-ttl-seconds":
                    metaTtl = TakeInt();
                    break;
                case "--enable-local-tools":
                    enableLocalTools = TakeBool();
                    break;
                default:
                    throw new OptionException($"Unknown option: {name}");
            }
        }

        var transportKind = transport.Trim().ToLowerInvariant() switch
        {
            "stdio" => TransportKind.Stdio,
            "streamable-http" => TransportKind.StreamableHttp,
            "both" => TransportKind.Both,
            "sse" => throw new OptionException("UnsupportedTransport: sse is not supported"),
            _ => throw new OptionException(
                $"Invalid --transport value: {transport} (expected stdio, streamable-http or both)"),
        };

        var environmentKind = environment.ToLowerInvariant() switch
        {
            "domestic" => CloudEnvironment.Domestic,
            "international" => CloudEnvironment.International,
            _ => throw new OptionException(
                $"Invalid --env value: {environment} (expected domestic or international)"),
        };

        if (oosTimeout < 1)
        {
            throw new OptionException($"Invalid --oos-timeout-seconds: {oosTimeout}");
        }

        if (metaTtl < 1)
        {
            throw new OptionException($"Invalid --meta-cache-ttl-seconds: {metaTtl}");
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
            OosTimeoutSeconds = oosTimeout,
            MetaCacheTtlSeconds = metaTtl,
            EnableLocalTools = enableLocalTools,
            Help = help,
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