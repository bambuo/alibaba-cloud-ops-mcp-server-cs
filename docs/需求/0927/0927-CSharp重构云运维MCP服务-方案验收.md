# 0927 CSharp重构云运维MCP服务 — 方案验收

> 需求清单：[0927-CSharp重构云运维MCP服务-需求清单.md](0927-CSharp重构云运维MCP服务-需求清单.md)
> 方案评估：[0927-CSharp重构云运维MCP服务-方案评估.md](0927-CSharp重构云运维MCP服务-方案评估.md)
> 实施方案：[0927-CSharp重构云运维MCP服务-实施方案.md](0927-CSharp重构云运维MCP服务-实施方案.md)
> 分支：`main`（commit 14f3821） ｜ 日期：2026-11-12 ｜ 状态：本机验收通过，待真账号联调

## 一、验收环境与方法

验收在本机完成，未使用真实阿里云账号。

| 项 | 要求 | 实际 |
|---|---|---|
| 操作系统 | Linux 或 macOS | macOS arm64（Darwin 25.6.0） |
| .NET SDK | 10.0 或以上 | 10.0.403 |
| 阿里云账号 | 具备 ECS、OOS、CMS、OSS 权限 | 未提供，相关用例未执行 |
| 测试地域 | `cn-hangzhou` | 仅用于端点与默认值断言 |
| MCP 客户端 | Qoder / Cline 任一 | 使用自建 stdio 客户端脚本完成 `initialize` 与 `tools/list` |
| 上游对照环境 | Python 3.10+ 与 `uv` | 本机未安装 `uv`，未做双跑 diff |

验收方法：

1. **工具面对账**：在同一启动参数下调用 C# 版 `tools/list`，与上游源码清点的工具集合比较。
2. **契约测试**：104 项 xUnit 测试覆盖参数解析、端点、凭证、schema、规则引擎、本地工具。
3. **行为测试**：真实账号链路未执行。
4. **安全测试**：stdio 模式下 stdout 协议可解析，日志写 stderr；错误消息经代码审查。

## 二、逐项结果

| 编号 | 验收项 | 期望 | 实际 | 结论 |
|---|---|---|---|---|
| A1 | 默认模式工具数 | 40 | 40（stdio `tools/list`） | 通过 |
| A2 | `--services` 工具数 | 44 | 44 | 通过 |
| A3 | `--code-deploy` 工具数 | 7 | 7 | 通过 |
| A4 | 工具名与描述逐字一致 | 一致 | 工具名一致；部分长参数描述为精简版 | 部分通过 |
| A5 | `inputSchema` 的 required / default 一致 | 一致 | 与上游 AST 全量比对 29 个静态工具：10 个描述文案差异；`OOS_RunInstances.Tags`、`CommonAPICaller.parameters` 默认值不同；`OOS_CodeDeploy.project_path` 已改为必填 | 部分通过 |
| A6 | 版本号与上游发布一致 | 0.9.27 | 包名、`serverInfo.version` 与文档均为 0.9.27 | 通过 |
| B1 | 9 个 CLI 参数默认值 | 一致 | 7 项单元测试通过 | 通过 |
| B2 | `--visible-tools` 大小写不敏感 | 一致 | 白名单 3 个工具数量与名称正确；小写静态名不再生成动态工具，`ECS_DescribeInstances` 白名单下使用静态 schema | 通过 |
| B3 | `--code-deploy` 与 `--visible-tools` 互斥 | 后者失效并告警 | 后者失效；未输出告警 | 部分通过 |
| C1 | 请求头凭证优先于环境变量 | 优先 | 单元测试通过 | 通过 |
| C2 | `--headers-credential-only` 不回退 | 不回退 | 单元测试通过 | 通过 |
| C3 | 端点映射与上游一致 | 一致 | 11 条用例通过，`EndpointResolver` 行覆盖 100% | 通过 |
| D1 | stdio 下 stdout 仅协议帧 | 无日志 | `initialize` 与 `tools/list` 均解析成功 | 通过 |
| D2 | 错误消息不含 AK/SK/Token | 不含 | 代码审查通过，未做自动化扫描 | 部分通过 |
| D3 | 无状态 HTTP 下动态工具与白名单生效 | 生效 | 默认 40、白名单 3、`--code-deploy` 7（HTTP 实测） | 通过 |
| E1 | OSS 输出字段与上游语义等价 | 等价 | 结构化 JSON 已实现，未真实调用 | 未执行 |
| E2 | 部署规则引擎输出一致 | 一致 | 规则用例通过，DeployRules 行 100.0% / 分支 94.5% | 通过 |
| E3 | 轮询可取消、有总超时 | 生效 | 已实现取消与 600 秒超时，未做集成验证 | 未执行 |
| F1 | `ECS_DescribeInstances` 真实调用 | 成功 | 未执行 | 未执行 |
| F2 | `OOS_RunCommand` 真实调用 | 成功 | 未执行 | 未执行 |
| F3 | `OOS_CodeDeploy` 全链路 | 成功 | 未执行 | 未执行 |
| G1 | 覆盖率 Cloud/Auth ≥ 80% | 达标 | ApiMeta 100%、Endpoint 100%、Credential 95.8%、Options 87.9%、ExtraConfig 84.6%、GenericApiCaller 行 98.5% / 分支 92.6% | 通过 |
| G2 | 规则引擎分支 ≥ 90% | 达标 | 分支 94.5% | 通过 |
| H1 | 文档五件套与索引校验 | 通过 | 两项脚本均通过，14 个文件 0 失败 | 通过 |

## 三、发现并修复的问题

文档阶段修复：

1. 五件套头部链接使用绝对路径，bootstrap 校验器按仓库根解析失败；改为同目录相对路径，两个校验器均通过。
2. 文档头元信息写在一行，不满足「`> 状态：`」「`> 证据基线：`」独立行的要求；拆分为多行。
3. 需求清单中的 `skill://` 链接被校验器当作文件路径；改为纯文本。

实现阶段修复：

1. `AddMcpServer` 中重新赋值 `ToolCollection` 覆盖了 `WithTools<T>()` 已注册的静态工具，导致默认模式只有 11 个动态工具；移除赋值后恢复为 40 个。
2. `WithTools<T>()` 在工具类未注册到 DI 时无法构造实例；补充 7 个工具类的单例注册。
3. 泛化调用需要 CMS 使用 `metrics.{region}` 端点，新增 `endpointOverride` 参数。
4. OSS V1 SDK 的 `ListObjectsRequest` 构造与 `ListBuckets` 返回类型与预期不同，按实际 API 修正。
5. 无状态 HTTP 每个请求重建工具集合，启动时注册的动态工具与白名单被丢弃（实测 29 个）；改为在 `McpServerOptions` 的 PostConfigure 阶段回填，修复后默认 40、白名单 3、`--code-deploy` 7。
6. `Lazy<Task>` 缓存会把一次瞬时元数据失败固定为永久失败，启动期随机缺失动态工具（实测出现 33 与 2）；失败时移出缓存并新增重试回归测试，之后连续实测稳定为 44/40/3。
7. 与上游逐项对照（`tools/list` schema + Python AST）发现 4 项 P1 偏差并修复：`CMS_GetDiskTotalData` 指标名改为 `diskusage_total`、`CMS_GetDiskUsedData` 改为 `diskusage_used`；`LOCAL_RunShellScript` 的 `shell=false` 改为按 POSIX 规则拆分后直接执行；部署状态改为读写同一项目根并统一文件名 `application.json`。
8. 同批修复 9 项 P2 契约偏差：`LOCAL_ListDirectory` 返回 `{path, items, count}` 并按名排序；`GetAPIInfo` 只返回参数数组；OOS 工具返回 `body`；`OOS_GetDeployStatus` 返回 `{info, status, execution_id, deploy_execution_info}`；部署成功与实例缺失响应补齐控制台链接和操作指引；bssopenapi 在 domestic 环境下不区分地域；应用/分组探测只把 `EntityNotExists.*` 视为不存在。
9. 新增 9 项行为测试（CMS 指标与维度、`shell=false` 与命令行拆分、目录返回结构、`GetAPIInfo`、OOS body 解包、存在性判定），测试数由 91 增至 100。
10. 全量语义核对后修复 3 项新差异：白名单命中静态工具名时跳过动态生成（白名单模式同时注册 `InstanceTools`，`ECS_DescribeInstances` 使用静态 schema）；`OOS_CodeDeploy.project_path` 改为必填；启动命令生成失败与 OSS 未开通改为返回上游同款结构化引导。新增 4 项测试，测试数增至 104。

Mermaid 图未做 CLI 渲染，仅通过结构检查，见未完成项。

## 四、验收数据处置

- 真实账号验证产生的 ECS 实例、Bucket、应用与分组，在验收结束后按清单逐项释放。
- 部署产物与 `.code_deploy/` 目录只保留在临时工作目录，不提交仓库。
- 凭据只通过环境变量注入，不写入文件、不进入日志。

## 五、未完成项

1. F1–F3 真实账号链路未执行，未提供测试账号。
2. E1、E3 只完成实现，未做真实 OSS/OOS 调用与取消验证。
3. Mermaid 图未做 CLI 渲染校验：本机无 `mmdc`。
4. 上游对照环境缺少 `uv`，未做 Python 与 C# 的双跑 diff。
5. 八项口径 Q1–Q8 未逐项书面确认，实现按实施方案默认值执行。
6. 审查中的 P3/P4 差异未处理：5 个 CMS 工具与 `OOS_StartRDSInstances` 的描述文案、长参数描述精简、打标策略（全量打标 vs 仅缺失）、损坏压缩包降级为 `unknown`、Golang 启动命令无条件 `|| true` 等。

## 六、全量语义核对（2026-11-12）

方法：

1. Python AST 抽取上游 34 个静态工具的函数签名、docstring、`Field` 描述与默认值。
2. 分别以默认、`--services`、`--code-deploy`、`--visible-tools` 启动 C# 版，抓取 `tools/list` 全量 schema。
3. 逐工具比较名称、描述、参数、必填、默认值；逐项阅读上游函数体，比对模板名、指标名、API 名、返回解包与错误分支。
4. 通过 MCP 协议实际调用离线可验证工具；云端工具标注验证等级。

结果：

| 项 | 结果 |
|---|---|
| 工具面 | 40/44/7/3 与上游一致；11 个动态工具名称一致 |
| instructions | 归一化后逐行相等（上游 2720 字符，差异为尾部换行） |
| PromptUnderstanding 资源 | `PROMPT_UNDERSTANDING.md` 逐字节相等（3611 字节） |
| 静态工具 schema | 29 个比对；19 个完全一致，10 个存在描述文案差异（其中 `OOS_CodeDeploy` 另有必填标记差异） |
| schema 结构性差异 | `OOS_RunInstances.Tags` 默认值、`CommonAPICaller.parameters` 默认值 |
| 动态 schema | 11 个工具的 required、`RegionId` 默认值、ECS 列表参数转数组均符合上游规则 |
| OOS 模板与参数 | 10 个模板名与参数结构逐项一致 |
| CMS | 9 个指标名、namespace、维度、端点一致且有断言 |
| 本地工具 | 3 个实际调用通过；目录排序、`shell` 两种模式、超时、技术栈识别均符合 |
| 通用工具 | PromptUnderstanding、ListAPIs、GetAPIInfo 实际调用符合；CommonAPICaller 无凭证时返回 `MissingCredentials` |
| 部署工具 | `GetDeployStatus`、`GetLastDeploymentInfo` 返回结构与上游一致；`CodeDeploy` 存在 8 项已知差异 |
| 构建与测试 | `dotnet build -warnaserror` 0 警告；104/104 测试通过 |

新增差异已当场修复：`--visible-tools` 小写静态名生成空 schema 动态工具、`project_path` 必填标记、启动命令失败与 OSS 未开通的引导载荷。

## 七、复现步骤

```bash
# 构建与测试
dotnet build
dotnet test

# stdio 工具面
ALIBABA_CLOUD_ACCESS_KEY_ID=<id> ALIBABA_CLOUD_ACCESS_KEY_SECRET=<secret> \
  dotnet run --project src/AlibabaCloud.Ops.Mcp.Server -- --transport stdio

# 文档校验
node ~/.agents/skills/requirement-docs/scripts/check-docs.mjs \
  --docs docs/需求 --issue 0927
```

## 八、结论

本机验收通过：工具面 40/44/7、CLI 参数、端点映射、凭证优先级、规则引擎、两项覆盖率目标与文档校验均达到预期。真实阿里云调用链路、OSS 输出结构、轮询取消尚未完成，留待下一阶段。