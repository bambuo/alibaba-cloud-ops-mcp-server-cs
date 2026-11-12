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
| `--transport` | string | `stdio` | `stdio` / `streamable-http` / `sse` | `sse` 首版返回明确错误并退出码 2 |
| `--port` | int | `8000` | 1–65535 | 仅 HTTP 传输生效 |
| `--host` | string | `127.0.0.1` | 合法地址 | 仅 HTTP 传输生效 |
| `--services` | string | 空 | 逗号分隔服务码 | 非空时额外注册 4 个通用工具 |
| `--headers-credential-only` | bool | `false` | 标志 | 启用后凭证只能来自请求头，不回退 |
| `--env` | string | `domestic` | `domestic` / `international` | 切换端点域名 |
| `--code-deploy` | bool | `false` | 标志 | 仅注册 7 个部署相关工具 |
| `--extra-config` | string | 空 | JSON 对象 | 追加服务与 API，累加不替换 |
| `--visible-tools` | string | 空 | 逗号分隔工具名 | 白名单，大小写不敏感；与 `--code-deploy` 互斥，后者优先 |

`--extra-config` 示例：`{"sls":["GetProject","ListProject"],"ecs":["StartInstance"]}`。单引号 Python 字面量不支持，解析失败时退出码 2 并打印 JSON 示例。

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
3. `--headers-credential-only` 为真且无请求头时，返回 `MissingCredentials`，不回退。

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
7. 用 `TimeProvider` 轮询 `ListExecutions`，间隔 1 秒，支持取消与总超时（默认 600 秒）。
8. 写入 `.code_deploy/.application.json`，返回控制台链接与部署结果。

启停命令：

- 用户提供 `applicationStart` / `applicationStop` 时优先使用。
- 未提供时由 `DeployRules` 按语言与产物类型生成，包含文件存在性检查、解压、`nohup` 后台运行与日志重定向。
- 规则表与上游逐条对齐，先用测试固定行为。

状态：`Running` 继续轮询；`Success`、`Failed`、`Cancelled` 结束；`Failed` 返回 `StatusMessage`。

## 六、错误与观测

| 场景 | 返回 | 日志 |
|---|---|---|
| 参数缺失或非法 | MCP `isError: true`，消息含参数名 | Warning，不含凭据 |
| 凭证缺失 | `MissingCredentials` | Warning |
| 阿里云 4xx | 保留 `Code` 与 `Message` | Error，含 RequestId |
| 阿里云 5xx / 限流 | SDK 重试后返回错误 | Error，含重试次数 |
| 元数据不可达 | 跳过动态工具 | Warning，含 URL |
| 本地命令超时 | 超时错误 + 已终止标记 | Warning |
| 部署执行失败 | `Failed` + 状态消息 + 控制台链接 | Error |

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
| E1 | OSS 返回值 | Python 对象字符串 | 结构化 JSON | 接受，验收按字段比对 |
| E2 | CMS 返回值 | SDK 对象 | JSON 数组 | 接受 |
| E3 | SSE | 支持 | 不支持，参数报错 | 接受，文档声明 |
| E4 | `--extra-config` | JSON 或 Python 字面量 | 仅 JSON | 接受 |
| E5 | 轮询 | 无总超时 | 600 秒默认超时 | 接受，可配置 |
| E6 | 元数据失败 | 启动失败 | 降级为静态工具 | 待确认 Q6 |
| E7 | `ForeceStop` | 拼写错误 | 保持原样 | 待确认 Q2 |
| E8 | `OSS_PutObject` | 未注册 | 不注册 | 待确认 Q3 |
| E9 | 工具注解 | 无 | 增加只读/破坏性标注 | 待确认，可能触发客户端确认 |

## 十三、实现结果（2026-11-12）

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build` 通过，Release 构建干净 |
| 测试 | 91 项通过，0 失败 |
| 工具面 | 默认 40、`--services` 44、`--code-deploy` 7、白名单 3（stdio 实测） |
| 传输 | stdio 与无状态 HTTP 工具面一致：默认 40、白名单 3、`--code-deploy` 7（HTTP 实测） |
| 版本 | 包版本与 `serverInfo.version` 均为 0.9.27，与上游发布号一致 |
| 打包 | `dotnet pack` 生成 7.1 MB nupkg；`dotnet tool install` 后可执行，`--transport sse` 退出码 2 |
| 覆盖率 | ApiMetaClient 100%、EndpointResolver 100%、CredentialProvider 95.5%、ServerOptions 87.9%、ExtraConfigParser 84.6%、DeployRules 行 100.0% / 分支 94.5%、GenericApiCaller 行 98.5% / 分支 92.6% |

与设计稿的实现差异：

1. OOS 与 CMS 固定工具统一走泛化调用，未使用产品 SDK；请求参数与上游一致，减少 4 个重依赖。
2. OSS 使用 V1 SDK `Aliyun.OSS.SDK.NetCore` 2.14.1；V2 0.3.0 仍为预览版，未采用。
3. OSS Bucket 使用确定性命名 `app-{应用名}-code-deploy`，未复刻上游按标签搜索复用。
4. `--visible-tools` 通过启动后从工具集合移除未命中项实现；`--code-deploy` 优先但不输出告警。
5. 部署轮询总超时固定 600 秒，未暴露为参数。

## 十四、待办

1. 真实阿里云账号联调 `ECS_DescribeInstances`、`OOS_RunCommand`、`OOS_CodeDeploy`。
2. HTTP 传输下请求头凭证与 stdio 日志隔离的端到端验证。
3. OSS 与 CMS 返回结构的真实样例对账。
4. 初始化 git 仓库并拆分提交。
5. 八项口径 Q1–Q8 书面确认。

验收结论与实测数据见 [0927-CSharp重构云运维MCP服务-方案验收.md](0927-CSharp重构云运维MCP服务-方案验收.md)。本机实现、测试与文档校验已完成；真实账号链路尚未执行。
