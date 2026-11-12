# 0927 CSharp重构云运维MCP服务 — 实施方案

> 需求清单：[0927-CSharp重构云运维MCP服务-需求清单.md](0927-CSharp重构云运维MCP服务-需求清单.md)
> 方案评估：[0927-CSharp重构云运维MCP服务-方案评估.md](0927-CSharp重构云运维MCP服务-方案评估.md)
> 方案验收：[0927-CSharp重构云运维MCP服务-方案验收.md](0927-CSharp重构云运维MCP服务-方案验收.md)
> 分支：未初始化 ｜ 日期：2026-11-12 ｜ 状态：已实现，本机构建与测试通过

## 一、目标

以 `net10.0` 实现与上游 v0.9.27 行为兼容的 MCP 服务。本文件承载参数契约、实现细节、改动清单与边界；方案取舍见方案评估。

## 二、外部契约

### 2.1 CLI 参数

| 参数 | 类型 | 默认值 | 取值 | 行为 |
|---|---|---|---|---|
| `--transport` | string | `stdio` | `stdio` / `streamable-http` / `both` / `sse` | `both` 同进程并行提供两种传输；`sse` 返回明确错误并退出码 2 |
| `--port` | int | `8000` | 1–65535 | 仅 HTTP 传输生效 |
| `--host` | string | `127.0.0.1` | 合法地址 | 仅 HTTP 传输生效 |
| `--services` | string | 空 | 逗号分隔服务码 | 非空时额外注册 4 个通用工具；保留原始大小写与重复项 |
| `--headers-credential-only` | bool | `false` | 标志，支持 `=false` 与分离值 `false` | 启用后凭证只能来自请求头，不回退；OSS 例外见下 |
| `--env` | string | `domestic` | `domestic` / `international` | 切换端点域名 |
| `--code-deploy` | bool | `false` | 标志，支持 `=false` 与分离值 `false` | 仅注册 7 个部署相关工具 |
| `--extra-config` | string | 空 | JSON 对象或 Python 单引号字面量 | 追加服务与 API，累加不替换；解析失败告警后忽略 |
| `--visible-tools` | string | 空 | 逗号分隔工具名 | 白名单，大小写不敏感；与 `--code-deploy` 互斥，后者优先 |
| `--oos-timeout-seconds` | int | `900` | ≥1 | OOS 执行轮询总超时 |
| `--meta-cache-ttl-seconds` | int | `600` | ≥1 | POP 元数据缓存 TTL |
| `--enable-local-tools` | bool | `true` | 标志，支持 `=false` 与分离值 `false` | 为 `false` 时不注册 `LOCAL_*` |
| `--help` / `-h` | flag | — | — | 打印用法并以退出码 0 结束 |

`--extra-config` 示例：`{"sls":["GetProject","ListProject"],"ecs":["StartInstance"]}`，也接受 `{'sls': ['GetProject']}`。解析失败时向 stderr 打印 `WARN: Failed to parse --extra-config, ignoring it: ...` 并继续启动，不阻断进程。

凭证例外：`--headers-credential-only` 只约束通用调用；OSS 与部署上传按上游语义始终回落环境变量与默认凭证链（`ICredentialProvider.ResolveForOss`）。请求头只要 `x-acs-accesskey-id` 非空即采用，secret 可缺失。

### 2.2 环境变量

| 变量 | 必填 | 用途 |
|---|---|---|
| `ALIBABA_CLOUD_ACCESS_KEY_ID` | 条件必填 | AK ID；凭证链的一环 |
| `ALIBABA_CLOUD_ACCESS_KEY_SECRET` | 条件必填 | AK Secret |
| `ALIBABA_CLOUD_SECURITY_TOKEN` | 否 | STS 临时令牌 |
| `ALIBABA_CLOUD_ROLE_ARN` 等 | 否 | 由 `Aliyun.Credentials` 默认链识别，按官方约定读取 |

### 2.3 HTTP 请求头

| 请求头 | 映射 |
|---|---|
| `x-acs-accesskey-id` | AccessKeyId |
| `x-acs-accesskey-secret` | AccessKeySecret |
| `x-acs-security-token` | SecurityToken |

仅在 HTTP 传输下生效。stdio 模式忽略这些请求头。

### 2.4 工具面

工具名称、数量与分组以 [02-功能清单](../../功能/02-功能清单.md) 为准：默认 40 个，`--services` 44 个，`--code-deploy` 7 个。

## 三、链路与生效点

| 功能点 | 图文档 | 生效点 |
|---|---|---|
| 启动注册与一次工具调用 | [01-MCP工具注册与调用-v1](../../流程/01-MCP工具注册与调用-v1.md) | `Program.cs`、`Tools/DynamicApiTool.cs`、`Cloud/GenericApiCaller.cs` |
| 代码部署 | [02-代码部署全流程-v1](../../流程/02-代码部署全流程-v1.md) | `Tools/CodeDeployTool.cs`、`Tools/DeployRules.cs` |
| 凭证与端点 | [03-凭证与端点解析-v1](../../流程/03-凭证与端点解析-v1.md) | `Auth/CredentialProvider.cs`、`Cloud/EndpointResolver.cs` |

## 四、核心实现

### 4.1 启动与注册

```csharp
var options = ServerOptions.Parse(args);              // 失败返回退出码 2
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdio 安全
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ICredentialProvider, CredentialProvider>();
builder.Services.AddSingleton<ApiMetaClient>();
builder.Services.AddSingleton<EndpointResolver>();
builder.Services.AddSingleton<GenericApiCaller>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "alibaba-cloud-ops-mcp-server", Version = "0.9.27" })
    .WithToolsFromAssembly(typeof(StaticTools).Assembly)
    .WithStdioServerTransport();                       // 或 WithHttpTransport + MapMcp("/mcp")
```

注册顺序（对齐上游）：

1. `--code-deploy`：只注册 7 个工具，不加载通用工具与动态工具。
2. `--visible-tools`：先注册命中的静态工具，再按白名单中的 `SERVICE_API` 形式生成动态工具，最后合并 `--extra-config` 中同样在白名单内的项。
3. 默认：静态工具（跳过静态 `ECS_DescribeInstances`）→ 动态工具 11 个 → 合并 `--extra-config`。
4. `--services` 非空时，额外注册 `PromptUnderstanding`、`ListAPIs`、`GetAPIInfo`、`CommonAPICaller`。

server instructions 与 `PROMPT_UNDERSTANDING.md` 原文嵌入，不做改写。

### 4.2 动态 API 工具

```csharp
public sealed class DynamicApiTool : McpServerTool
{
    private readonly ToolDescriptor _descriptor;   // name / description / inputSchema
    private readonly GenericApiCaller _caller;

    public override Tool ProtocolTool => _descriptor.ToProtocolTool();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext context, IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default);
}
```

Schema 生成规则：

| 项 | 规则 |
|---|---|
| 工具名 | `{SERVICE.ToUpperInvariant()}_{API}`，API 保留元数据大小写 |
| 描述 | 元数据 `summary` |
| 参数过滤 | 名称含 `.` 的参数跳过 |
| 必填 | `schema.required == true` 进入 `required` |
| 类型映射 | string / integer / boolean / array / object / number 原样映射 |
| 参数描述 | `{description} 参数类型: {type},参数示例：{example}` |
| ECS 列表参数 | 15 个已知参数（`InstanceIds`、`SecurityGroupIds` 等）声明为 array |
| RegionId | 缺失时补 `string`，默认 `cn-hangzhou` |
| 元数据不可达 | 跳过该工具并记录 warning，不阻断启动 |

### 4.3 泛化调用

```csharp
public sealed class GenericApiCaller
{
    public Task<Dictionary<string, object>> CallAsync(
        string service, string api, IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken);
}
```

映射规则（对齐上游 `_tools_api_call`）：

| 项 | 规则 |
|---|---|
| Version | 元数据 `defaultVersion`，按服务码大小写不敏感匹配 |
| Method | 元数据 `methods[0] == "post"` 时 POST，否则 GET |
| Pathname | 元数据 `path`，缺省 `/` |
| Style | 元数据 `style`，缺省 `RPC` |
| ReqBodyType | `formData` |
| BodyType | `json` |
| 参数 | 过滤 `null`；通过 `OpenApiUtil.Client.Query(dict)` 放入 query |
| ECS 列表参数 | `json.dumps` 等价，序列化为 JSON 字符串 |
| User-Agent | `alibaba-cloud-ops-mcp-server` |
| 重试 | 使用 SDK 默认策略；不复刻上游针对 `Errno 9` 的 Python 专用重试 |

返回 `body` / `headers` / `statusCode`，以 JSON 序列化为工具结果。

### 4.4 凭证与端点

凭证优先级：

1. HTTP 请求头存在 `x-acs-accesskey-id` 时使用请求头三元组。
2. 否则使用 `Aliyun.Credentials` 默认链（环境变量 → 配置文件 → RAM 角色 → 实例元数据）。
3. `--headers-credential-only` 为真且无请求头时，返回 `CredentialMissing`，不回退；OSS 例外见 2.1。

端点规则：

| 服务类别 | 判定 | 端点 |
|---|---|---|
| 区域级 | `ecs`、`oos`、`vpc`、`slb` | `{service}.{region}.aliyuncs.com` |
| 双端点 | `rds`、`ess`、`dds`、`r-kvstore` | 地域在白名单内用 `{service}.aliyuncs.com`，否则 `{service}.{region}.aliyuncs.com` |
| 中心化 | `cbn`、`ros`、`ram` | `{service}.aliyuncs.com` |
| 计费 | `bssopenapi` | 国内 `business.aliyuncs.com`；国际 `business.ap-southeast-1.aliyuncs.com` |
| 其他 | 默认 | `{service}.{region}.aliyuncs.com` |

双端点地域白名单：

- `rds`：cn-qingdao、cn-beijing、cn-hangzhou、cn-shanghai、cn-shenzhen、cn-heyuan、cn-guangzhou、cn-hongkong
- `ess`：cn-qingdao、cn-beijing、cn-hangzhou、cn-shanghai、cn-nanjing、cn-shenzhen
- `dds`：cn-qingdao、cn-beijing、cn-wulanchabu、cn-hangzhou、cn-shanghai、cn-shenzhen、cn-heyuan、cn-guangzhou
- `r-kvstore`：cn-qingdao、cn-beijing、cn-wulanchabu、cn-hangzhou、cn-shanghai、cn-shenzhen、cn-heyuan

### 4.5 静态工具

| 模块 | 实现方式 | 说明 |
|---|---|---|
| OOS 10 个 | 产品 SDK `StartExecution` + 状态轮询 | 模板名与参数结构照抄上游 |
| CMS 9 个 | 产品 SDK `DescribeMetricLast` | namespace `acs_ecs_dashboard`，维度为 instanceId |
| OSS 4 个 | OSS SDK | 返回值改结构化 JSON，属已声明差异 |
| 部署 3 个 | OOS + ECS + OSS SDK | 见下一节 |
| LOCAL 3 个 | `System.Diagnostics.Process` + 文件 API | 见下一节 |

## 五、本地工具与部署实现

### 5.1 LOCAL_ListDirectory

- 输入：`path`、`recursive=false`。
- 输出：`name`、`path`、`type`（`directory` / `file`）、`size`（文件字节数，目录为 `null`）、`modified`（Unix 秒，浮点）。
- 不可访问的条目跳过，不中断整体列举。
- 路径不存在或不是目录时返回工具错误。

### 5.2 LOCAL_RunShellScript

- 输入：`script`、`working_directory`、`timeout=300`、`shell=true`。
- 执行：Unix 使用 `/bin/sh -c`；Windows 使用 `cmd.exe /c`。`shell=false` 时按空白分词直接执行，首版不支持引号转义。
- 输出：`command`、`exit_code`、`stdout`、`stderr`、`success`。
- 超时：终止整个进程树，返回错误；捕获 `Win32Exception` 映射为「命令不存在」。
- 编码：UTF-8，去除 BOM。
- 安全：默认不限制工作目录；`--restrict-local-root` 为待确认项，启用后校验 `Path.GetFullPath` 落在指定根目录内。

### 5.3 LOCAL_AnalyzeDeployStack

- 输入：`directory`。
- 探测文件表：Node（package.json、lock、nvmrc）、Python（requirements、Pipfile、pyproject、setup.py、environment.yml、.python-version）、Java（pom.xml、build.gradle[.kts]）、Go（go.mod、go.sum、Gopkg.toml）、Rust、PHP、Ruby、Docker（Dockerfile、docker-compose）、Makefile、CMakeLists。
- 读取版本：`package.json.engines.node`、`.python-version`、`go.mod` 的 `go x.y`、Dockerfile 的 `FROM node|python`。
- 输出字段与上游一致：`directory`、`deployment_methods`、`package_managers`、`frameworks`、`runtime_versions`、`config_files`、`detected`。
- 未识别时 `deployment_methods = ["unknown"]`。

### 5.4 OOS_CodeDeploy

```csharp
public Task<DeployResult> DeployAsync(
    string name, string deployRegionId, string applicationGroupName,
    string objectName, string filePath, string deployLanguage, int port,
    string? projectPath, string? applicationStart, string? applicationStop,
    IReadOnlyList<string>? instanceIds, CancellationToken cancellationToken);
```

流程：

1. 规范化项目路径，确保 `.code_deploy/release/` 存在；产物不在该目录时复制过去。
2. 未提供实例 ID 时返回创建 ECS 的引导信息，不自动查询实例。
3. 上传产物到 OSS（`IArtifactStore`），记录 object key。
4. 检查应用与分组是否存在，缺失时创建。
5. 需要导入已有实例时调用 `TagResources` 打标。
6. 调用 `DeployApplicationGroup`，返回 execution id。
7. 用 `TimeProvider` 轮询 `ListExecutions`，间隔 1 秒，支持取消与总超时（默认 900 秒，`--oos-timeout-seconds` 可配）。
8. 写入 `.code_deploy/application.json`，返回控制台链接与部署结果。

启停命令：

- 用户提供 `applicationStart` / `applicationStop` 时优先使用。
- 未提供时由 `DeployRules` 按语言与产物类型生成，包含文件存在性检查、解压、`nohup` 后台运行与日志重定向。
- 规则表与上游逐条对齐，先用测试固定行为。

状态：`Running` 继续轮询；`Success`、`Failed`、`Cancelled` 结束；`Failed` 返回 `StatusMessage`。

## 六、错误与观测

| 场景 | 返回 | 日志 |
|---|---|---|
| 统一错误载荷 | `isError: true`，文本为 JSON `{"Code","Message","RequestId"?}`，结构与上游异常体一致，与 Java 侧逐字段一致 | 不含凭据 |
| 参数缺失或非法 | `Code=InvalidParameter`，消息含参数名 | Warning |
| 凭证缺失 | `Code=CredentialMissing` | Warning |
| 阿里云 4xx/5xx | 保留 SDK 的 `Code` 与 `Message`，可取到时带 `RequestId` | Error |
| 元数据不可达 | 泛化调用降级为 `POST / + RPC` 继续；动态工具注册失败则跳过该项 | Warning，含路径 |
| 元数据重试 | 最多 3 次，退避 300/600ms，耗尽后 `Code=DynamicMetaUnavailable` | Warning |
| 本地命令超时 | `Code=Timeout`，消息 `Command execution timeout (exceeded N seconds)` | Warning |
| OOS 执行失败 | `Code=Execution.Failed`，消息 `OOS Execution Failed, reason: {StatusMessage}.` | Error |
| OOS 轮询超时 | `Code=Timeout`，消息含执行 ID 与超时秒数 | Error |
| 产物不存在 | `Code=ResourceNotFound`，消息 `File does not exist: {path}` | Error |
| 部署信息写盘失败 | 只记日志，部署仍按成功返回 | Error |

- 所有日志写 stderr。
- 部署轮询通过 MCP progress 通知上报阶段与次数（客户端支持时）。
- 不在日志中输出 `AccessKeySecret`、`SecurityToken`、完整请求头。

## 七、改动清单

以下文件已于 2026-11-12 创建：

| 路径 | 类型 | 说明 |
|---|---|---|
| `src/AlibabaCloud.Ops.Mcp.Server/Program.cs` | 新增 | CLI、Host、传输、注册 |
| `Options/ServerOptions.cs` | 新增 | 9 个参数的解析与校验 |
| `Mcp/ServerInstructions.cs` | 新增 | 原文 instructions |
| `Auth/CredentialProvider.cs` | 新增 | 请求头与默认凭证链 |
| `Cloud/EndpointResolver.cs` | 新增 | 端点映射表 |
| `Cloud/ApiMetaClient.cs` | 新增 | 元数据拉取与缓存 |
| `Cloud/GenericApiCaller.cs` | 新增 | 泛化调用 |
| `Tools/OosTools.cs` | 新增 | 10 个 OOS 工具 |
| `Tools/CmsTools.cs` | 新增 | 9 个 CMS 工具 |
| `Tools/OssTools.cs` | 新增 | 4 个 OSS 工具 |
| `Tools/LocalTools.cs` | 新增 | 3 个本地工具 |
| `Tools/DeployRules.cs` | 新增 | 启停命令规则引擎 |
| `Tools/CodeDeployTool.cs` | 新增 | 部署编排 |
| `Tools/DynamicApiTool.cs` | 新增 | 运行时工具定义 |
| `Tools/CommonTools.cs` | 新增 | 4 个元数据/通用工具 |
| `Resources/PROMPT_UNDERSTANDING.md` | 新增 | 嵌入资源 |
| `tests/...` | 新增 | xUnit + WireMock.Net |

## 八、边界

- 不含 Web 前端。第五份文档按 MCP 客户端契约编写，沿用「前端对接文档」类型名。
- 不含数据库，`sql/` 为空。
- 不新增工具，不修改工具名、参数名、描述与提示词。
- 不实现 SSE，不实现 AOT 与单文件发布。
- 不修改上游仓库，不在上游提交 PR。
- 不涉及生产部署与发布。

## 九、测试计划

| 层级 | 范围 | 工具 |
|---|---|---|
| 单元 | 端点解析、schema 映射、ECS 列表参数序列化、路径校验、规则引擎、启停命令生成 | xUnit |
| 契约 | `tools/list` 名称与 schema、`tools/call` 参数绑定与错误返回 | xUnit + 内存传输 |
| 集成 | 元数据接口、OpenAPI、OSS 上传（mock） | WireMock.Net |
| 冒烟 | `ECS_DescribeInstances`、`OOS_RunCommand`、`OOS_CodeDeploy` | 真实账号，环境变量缺失时跳过 |

覆盖率目标：Cloud/Auth 与规则引擎 ≥ 80%，规则引擎分支 ≥ 90%。

## 十、里程碑

| 阶段 | 交付 | 人日 |
|---|---|---|
| M0 | 三项技术验证结论 | 1 |
| M1 | 启动、注册、1 个动态工具可调用 | 2 |
| M2 | OOS + CMS + OSS 工具 | 2.5 |
| M3 | 本地工具与规则引擎 | 3.5 |
| M4 | 部署编排与轮询 | 2.5 |
| M5 | 测试、打包、文档 | 3.5 |
| M6 | 工具面对账与冒烟 | 1.5 |

## 十一、兼容例外清单

| 编号 | 项 | 上游 | C# | 处理 |
|---|---|---|---|---|
| E1 | OSS 返回值 | Python 对象字符串 | 结构化 JSON，字段名与顺序对齐 | 接受，语言自有表达 |
| E2 | CMS 返回值 | SDK `datapoints` 原值 | 同取 `Datapoints` 原值 | 已对齐，不再是差异 |
| E3 | SSE | 支持 | 不支持，参数报错 | 接受，文档声明 |
| E4 | `--extra-config` | JSON 或 Python 字面量 | 两者均支持，失败告警后忽略 | 已对齐 |
| E5 | 轮询 | 无总超时 | 默认 900 秒，`--oos-timeout-seconds` 可配 | 接受，与 Java 一致 |
| E6 | 元数据失败 | 启动失败 | 泛化调用降级 `POST / + RPC`；动态工具跳过该项 | 已对齐 Java |
| E7 | `ForeceStop` | 拼写错误 | 保持原样 | 待确认 Q2 |
| E8 | `OSS_PutObject` | 未注册 | 不注册 | 待确认 Q3 |
| E9 | 工具注解 | 无 | 仅动态工具标注只读/破坏性/幂等 | 与 Java 一致，静态工具两侧均无注解 |
| E10 | 错误封装 | 异常体为 `{"Message","Code"}` JSON（键序 Message、Code，无 RequestId）；FastMCP 是否再加 `Error calling tool '<name>': ` 前缀未实测 | JSON `{Code,Message,RequestId?}` | 结构与上游一致，键序与 RequestId 处理不同 |
| E11 | OSS 建桶/删桶响应元数据 | 含 `request_id`/`headers` | .NET SDK 不暴露，`request_id` 为空串、`headers` 为空对象 | SDK 限制，字段集保持一致 |
| E12 | OSS 桶扩展字段 | ListBuckets 直接返回 | .NET SDK 只解析 name/location/creation_date，其余按桶 `GetBucketInfo` 补齐，无权限时为 null | SDK 限制，尽力补齐 |
| E13 | OSS User-Agent | 追加服务名后缀 | .NET SDK `ClientConfiguration.UserAgent` 只读，保留 SDK 默认 | SDK 限制 |
| E14 | 必填参数校验位置 | 框架层 | Java 由 MCP SDK 前置校验（纯文本），C# 在处理器内校验（结构化 JSON） | 框架自有行为，拒绝语义一致 |
| E15 | 静态工具 schema 形态 | pydantic `title`/`anyOf`/`items:{}` | 由 MCP C# SDK 按方法签名生成：无 `title`，可空用 `type:["string","null"]`，数组 `items` 带元素类型 | 语言自有表达；参数名、必填集合、默认值与描述一致。动态工具 schema 为手写，已逐项对齐 |

## 十三、实现结果（2026-11-12）

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build` 通过，0 警告 0 错误 |
| 测试 | 132 项通过，0 失败（含 21 项与 Java 对齐的语义回归） |
| 工具面 | 默认 40、`--services` 44、`--code-deploy` 7、`--enable-local-tools false` 37（stdio 实测，与 Java 逐项一致） |
| 传输 | stdio / streamable-http / both 均可用；both 下 stdio 与 HTTP 同时握手成功（实测） |
| instructions | 2099 字符，与 Java 侧逐行相等（实测） |
| 真实只读联调 | `ECS_DescribeRegions`、`OSS_ListBuckets`、`CommonAPICaller` 嵌套 Tag 展开成功；headers-only 下 OSS 可用 |
| 版本 | 包版本与 `serverInfo.version` 均为 0.9.27，与上游发布号一致 |
| 打包 | `dotnet pack` 生成 nupkg；`dotnet tool install` 后可执行，`--transport sse` 退出码 2 |
| 覆盖率 | ApiMetaClient 100%、EndpointResolver 100%、CredentialProvider 95.8%、ServerOptions 87.9%、ExtraConfigParser 84.6%、DeployRules 行 100.0% / 分支 94.5%、GenericApiCaller 行 98.5% / 分支 92.6% |

与设计稿的实现差异：

1. OOS 与 CMS 固定工具统一走泛化调用，未使用产品 SDK；请求参数与上游一致，减少 4 个重依赖。
2. OSS 使用 V1 SDK `Aliyun.OSS.SDK.NetCore` 2.14.1；V2 0.3.0 仍为预览版，未采用。对象列举用 V1 Marker 翻页（与 Java 的 V2 continuationToken 同为取全量，属 SDK 自有表达）。
3. OSS 部署 Bucket 已改为按 `app_management=code_deploy` 标签复用，未命中时以 `code-deploy-{8位uuid}` 命名并打标，与上游/Java 一致。
4. `--visible-tools` 通过启动后从工具集合移除未命中项实现；`--code-deploy` 优先，Java 侧输出 warn。
5. 部署轮询总超时默认 900 秒，由 `--oos-timeout-seconds` 配置；部署链本身与上游一致地不轮询。
6. 错误载荷统一为结构化 JSON `{Code,Message,RequestId?}`，错误码表与 Java 对齐。
7. HTTP 传输改为有会话（`Stateless=false`），与 Java 的会话隔离语义一致。
8. `.NET` SDK 未暴露的 OSS 响应元数据与 User-Agent 见例外清单 E11–E13。

## 十四、待办

1. 真实阿里云账号联调 `ECS_DescribeInstances`、`OOS_RunCommand`、`OOS_CodeDeploy`。
2. HTTP 传输下请求头凭证与 stdio 日志隔离的端到端验证。
3. OSS 与 CMS 返回结构的真实样例对账。
4. 八项口径 Q1–Q8 书面确认。

验收结论与实测数据见 [0927-CSharp重构云运维MCP服务-方案验收.md](0927-CSharp重构云运维MCP服务-方案验收.md)。本机实现、测试与文档校验已完成；真实账号链路尚未执行。
