# 0927 CSharp重构云运维MCP服务 — 方案评估

> 需求清单：[0927-CSharp重构云运维MCP服务-需求清单.md](0927-CSharp重构云运维MCP服务-需求清单.md)
> 实施方案：[0927-CSharp重构云运维MCP服务-实施方案.md](0927-CSharp重构云运维MCP服务-实施方案.md)
> 方案验收：[0927-CSharp重构云运维MCP服务-方案验收.md](0927-CSharp重构云运维MCP服务-方案验收.md)
> 分支：未初始化 ｜ 日期：2026-11-12 ｜ 状态：设计稿，待评审

## 一、要解决的问题

上游服务以 Python 实现，依赖 `uv`、Python 3.10+ 与 FastMCP 运行时。需要在 .NET 环境中提供同等能力的 MCP 服务，并满足：

1. 工具面与调用契约不变，现有 MCP 客户端无需改配置即可切换。
2. 单一 .NET 运行时，可通过 `dotnet tool` 或容器分发。
3. 保留本地部署编排能力，而不是只做云 API 转发。

性能不是主要动机。该服务的耗时集中在阿里云网络往返，Python 与 .NET 的解释器差异可以忽略。

## 二、约束事实（含实测证据）

| 事实 | 数值 | 取证方式 |
|---|---|---|
| 上游源码规模 | 3,530 行 Python | `find src -name '*.py' \| xargs wc -l` |
| 上游测试规模 | 4,300 行 / 290 个用例 | `find tests -name '*.py' \| xargs wc -l`；`grep -c '^def test_'` |
| 默认工具数 | 40 | 静态 29 + 动态 11；`@tools.append` 与 `config.py` 清点 |
| `--services` 工具数 | 44 | 追加 4 个元数据/通用工具 |
| `--code-deploy` 工具数 | 7 | 代码注册 7 个，上游文档写 6 个 |
| CLI 参数 | 9 个 | `server.py` 的 click 选项 |
| 动态工具机制 | 启动时拉取 `api.aliyun.com/meta/v1` | `alibabacloud/api_meta_client.py` |
| 部署规则引擎 | 约 900 行纯逻辑 | `application_management_tools.py` 的 `_analyze_deployment_file`、`_find_executable_files`、`_generate_*_command_by_rules` |
| 本机 .NET | 10.0.403；11.0.100 为 RC | `dotnet --list-sdks` |
| 本机 uv | 未安装 | `which uv uvx` |

NuGet 版本核实（2026-11-12，`api.nuget.org/v3-flatcontainer`）：

| 包 | 最新版本 | 备注 |
|---|---|---|
| `ModelContextProtocol` | 2.4.0 | 官方 C# SDK，51 个版本 |
| `AlibabaCloud.OpenApiClient` | 0.2.3 | 泛化调用核心包 |
| `AlibabaCloud.SDK.Oos20190601` | 3.8.0 | 产品 SDK |
| `AlibabaCloud.SDK.Cms20190101` | 4.0.12 | 产品 SDK |
| `AlibabaCloud.SDK.Ecs20140526` | 7.11.9 | 产品 SDK |
| `AlibabaCloud.SDK.Vpc20160428` | 7.3.2 | 产品 SDK |
| `AlibabaCloud.SDK.Rds20140815` | 16.0.0 | 产品 SDK |
| `AlibabaCloud.OSS.V2` | 0.3.0 | 仅 6 个版本，处 0.x |
| `Aliyun.OSS.SDK.NetCore` | 2.14.1 | V1，稳定但老 |
| `Aliyun.Credentials` | 1.5.4 | 默认凭证链 |

关键技术事实：

- `McpServerToolCreateOptions` 公开属性包含 `OutputSchema`，**不含 `InputSchema`**（官方 v1 API 文档）。
- 社区实践通过给 `ProtocolTool.InputSchema` 赋值，或继承 `McpServerTool` 提供运行时 schema；后者属于文档列出的受支持扩展点。
- 阿里云 .NET 支持泛化调用：`AlibabaCloud.OpenApiClient.Client.CallApi(Params, OpenApiRequest, RuntimeOptions)`，覆盖 RPC 与 ROA，与上游 `tea-openapi` 一一对应。

## 三、候选方案与排除

### 3.1 MCP 框架

| 候选 | 结论 | 理由 |
|---|---|---|
| `ModelContextProtocol` 2.4.0 | 采用 | 官方维护；stdio 与 ASP.NET Core 传输；支持运行时工具集合；与 .NET 10 兼容 |
| 自研 JSON-RPC / MCP 协议 | 排除 | 协议版本、通知、取消、进度均需自维护，无差异化收益 |
| 社区第三方 C# MCP 库 | 排除 | 维护活跃度与长期兼容性不如官方 SDK |
| C# 进程包装 Python 版 | 排除 | 非重构；双运行时；部署与故障面更大。仅可作为过渡对照基线 |

### 3.2 阿里云调用方式

| 候选 | 结论 | 理由 |
|---|---|---|
| 泛化调用 `AlibabaCloud.OpenApiClient` | 采用（通用层） | 与上游 `_tools_api_call` 结构一致；支持任意服务与 API；适配动态工具 |
| 强类型产品 SDK | 采用（固定工具） | CodeDeploy、TagResources、CMS 等请求字段多，强类型降低拼装错误 |
| 手写 `HttpClient` + V3 签名 | 排除 | 签名、重试、代理、凭证链全部自制，风险与工作量都更高 |
| 官方 OpenAPI MCP Server 替代 | 排除 | 无本地工具、部署编排与动态白名单，能力面不等价 |

### 3.3 OSS

| 候选 | 结论 | 理由 |
|---|---|---|
| `AlibabaCloud.OSS.V2` 0.3.0 | 首选 | 与上游 `alibabacloud_oss_v2` 语义接近；API 仍可能变动 |
| `Aliyun.OSS.SDK.NetCore` 2.14.1 | 备选 | 稳定，但与 V2 语义有差异 |
| 泛化调用 OSS | 排除 | OSS 签名体系与 OpenAPI 不同 |

决策：定义 `IArtifactStore`，默认实现基于 V2；若技术验证失败切 V1，上层不改。

### 3.4 凭证

| 候选 | 结论 | 理由 |
|---|---|---|
| 环境变量 + `Aliyun.Credentials` 默认链 | 采用 | 等价于上游 `CredClient`，支持 RAM 角色与实例元数据 |
| 仅环境变量 | 排除 | 丢失配置文件与 RAM 角色场景 |
| 请求头凭证 | 采用（HTTP 模式） | 上游支持 `x-acs-accesskey-*`；`--headers-credential-only` 时不回退 |

### 3.5 传输

| 候选 | 结论 | 理由 |
|---|---|---|
| stdio | 采用 | MCP 客户端最常用；与 `dotnet tool` 分发匹配 |
| Streamable HTTP | 采用 | 当前规范推荐；支持请求头凭证 |
| SSE | 暂不采用 | 规范已由 Streamable HTTP 取代；C# SDK 支持情况需验证 |

### 3.6 CLI 解析

| 候选 | 结论 | 理由 |
|---|---|---|
| `System.CommandLine` | 首选 | 9 个参数、含标志与枚举，内建帮助与校验 |
| 手写解析 | 备选 | 依赖更少，但帮助信息与错误提示需自维护 |
| Spectre.Console.Cli / Cocona | 排除 | 为 9 个参数引入额外 UI 依赖，收益不足 |

## 四、采用的方案

**档位 A：完全替换。**

1. 静态工具 29 个，使用特性标注或工厂方法注册，描述与参数名照抄上游。
2. 动态 API 工具 11 个，使用 `McpServerTool` 子类，在启动时从元数据构建 `InputSchema`；支持 `--extra-config` 追加。
3. 通用层统一走泛化调用；部署与 CMS 走产品 SDK；OSS 走独立 SDK。
4. 凭证、端点、工具注册顺序按上游逻辑复刻，差异项进入例外清单。
5. 传输首版为 stdio + Streamable HTTP。

选择理由：

- 需求明确要求现有客户端可无改配置切换，工具面必须一致。
- 动态工具虽然有实现成本，但它是 `--extra-config` 与 11 个内置 API 工具的唯一通用实现方式；改为手写会引入 11 份重复 schema。
- 泛化调用与上游同构，迁移时可按函数一一对照，降低行为漂移。

## 五、工作量

| 阶段 | 内容 | 人日 |
|---|---|---|
| M0 | 技术验证：动态工具 schema、请求头凭证、OSS V2 上传 | 1 |
| M1 | 骨架、CLI、传输、凭证、端点、元数据、1 个动态工具打通 | 2 |
| M2 | OOS 10 + CMS 9 + OSS 4 | 2.5 |
| M3 | LOCAL 3 + 部署规则引擎 | 3.5 |
| M4 | CodeDeploy 编排、产物上传、轮询与取消 | 2.5 |
| M5 | 测试、打包、Docker、文档 | 3.5 |
| M6 | 与上游工具面对账、真账号冒烟 | 1.5 |
| 合计 | | 16.5（区间 15–22） |

估算前提：1 名熟悉 .NET 与 MCP 的开发者；不含真实账号审批等待；包含测试移植。

## 六、风险与观察项

| 风险 | 观察信号 | 处置 |
|---|---|---|
| 动态 `InputSchema` 在 2.4.0 不可用 | `tools/list` 缺业务参数或调用无法绑定 | M0 验证；失败降级为 11 个手写静态工具 |
| OSS V2 0.3.0 不稳定 | 编译失败、上传行为不符 | 切 V1，保持 `IArtifactStore` 不变 |
| 规则引擎漂移 | 同输入生成的启停命令不同 | 先移植规则测试，再做 fixture 对账 |
| 请求头凭证拿不到 | `IHttpContextAccessor` 为空 | 仅支持环境变量模式并文档说明 |
| stdio 输出污染 | 客户端协议解析失败 | 日志强制 stderr，加断言测试 |
| 上游缺陷固化 | 评审反复提出拼写与数量问题 | 以例外清单为准，不临时改口径 |

## 七、复现方式

```bash
# 1. 取证上游
git clone --depth 1 https://github.com/aliyun/alibaba-cloud-ops-mcp-server /tmp/ops-mcp-ref
cd /tmp/ops-mcp-ref && git rev-parse HEAD
find src -name '*.py' | xargs wc -l
find tests -name '*.py' | xargs wc -l

# 2. 清点工具
grep -rn '@tools.append' src/alibaba_cloud_ops_mcp_server/tools/

# 3. 核实 NuGet 版本
curl -s https://api.nuget.org/v3-flatcontainer/modelcontextprotocol/index.json | tail -c 200
curl -s https://api.nuget.org/v3-flatcontainer/alibabacloud.oss.v2/index.json | tail -c 200
```

## 八、未验证项

1. `McpServerTool` 子类在 2.4.0 上的完整行为，仅依据官方文档与社区讨论。
2. `.NET` 阿里云 SDK 对 AOT 与裁剪的兼容性，未测试。
3. OSS V2 0.3.0 的上传与分片能力，未实测。
4. 上游 OOS 模板在实际账号下的执行时长，未实测。
