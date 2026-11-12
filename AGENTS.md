# AGENTS.md

## 项目性质

本仓库是 `aliyun/alibaba-cloud-ops-mcp-server` v0.9.27 的 C# 重写项目，目标为行为兼容替换。当前阶段：代码已实现，104 项单元测试通过；未做真实账号联调。

- 上游基线：commit `751a8fd7fa9abeac84bbf86f65175ddd13948a7d`（2026-10-27），版本 0.9.27，Apache-2.0。
- 保真档位：A 完全替换（40 个工具、schema、`--extra-config` 动态注册全部对齐）。
- 需求编号：0927。

## 角色与边界

1. 默认只写文档，不改业务代码、不改数据库、不改对外契约。
2. 新建代码、重命名、删除文件、提交、推送、部署均需明确授权。
3. 上游缺陷是否修正，以 [需求 0927 实施方案](docs/需求/0927/0927-CSharp重构云运维MCP服务-实施方案.md) 的例外清单为准，不擅自"顺手优化"。
4. 文档中的状态必须可核：未执行写"未执行"，未验证写"未验证"，禁止写"已完成"。

## 阅读顺序

1. [README.md](README.md)：项目定位与技术栈。
2. [docs/架构全景图.md](docs/架构全景图.md)：系统边界与运行时链路。
3. [docs/功能/02-功能清单.md](docs/功能/02-功能清单.md)：工具面与统计口径。
4. [docs/功能/03-领域术语.md](docs/功能/03-领域术语.md)：术语与代码映射。
5. [docs/需求/0927/](docs/需求/0927/)：需求、评估、实施、验收、对接五件套。
6. [docs/流程/](docs/流程/)：按功能点的流程图与状态机。

## 仓库地图

| 路径 | 职责 |
|---|---|
| `src/AlibabaCloud.Ops.Mcp.Server/` | C# 服务实现 |
| `tests/AlibabaCloud.Ops.Mcp.Server.Tests/` | xUnit 测试 |
| `docs/需求/0927/` | 本需求的五件套；`assets/` 放参考图，`sql/` 放表结构变更 |
| `docs/流程/` | 功能点级流程图、时序图、状态机 |
| `docs/功能/` | 架构、功能清单、术语、限制 |
| `docs/README.md` | 总索引，唯一入口 |

## 常用命令

```bash
# 构建与测试
dotnet build
dotnet test

# 打包为 dotnet tool
dotnet pack -c Release -o nupkg

# 文档校验
node ~/.agents/skills/requirement-docs/scripts/check-docs.mjs \
  --docs docs/需求 --issue 0927
```

## 架构约定（规划）

- 单一可执行项目 + 测试项目，不提前拆分多程序集。
- 依赖方向：`Tools -> Cloud/Auth`；`Cloud -> AlibabaCloud.OpenApiClient / 产品 SDK`。`Tools` 不直接持有 `HttpClient`。
- 所有外部调用必须接受 `CancellationToken`；轮询使用注入的 `TimeProvider`，禁止 `Thread.Sleep`。
- stdio 模式下 stdout 只允许 MCP 协议帧，日志一律写 stderr。
- 工具描述与 server instructions 使用上游原文，不翻译、不改写；它们是模型行为的一部分。
- 动态 API 工具通过 `McpServerTool` 子类提供运行时 `InputSchema`，不使用反射生成。
- 阿里云调用优先泛化调用；仅 CodeDeploy、TagResources、CMS 等复杂请求使用强类型产品 SDK。

## 编码约定

- 目标框架 `net10.0`，启用 `Nullable`，警告视为错误。
- 异步全链路，禁止 `.Result` / `.Wait()`。
- 异常在工具边界转换为 MCP 错误结果，消息中不得包含 AK、SK、安全令牌或完整请求头。
- 新增配置项必须有默认值，并写入实施方案的参数表。

## 测试约定

- 纯逻辑（端点解析、schema 映射、部署规则、命令生成、路径校验）必须有单元测试。
- 外部 HTTP 用 WireMock.Net 模拟，不打真实阿里云接口。
- 需要真实 AK 的用例在环境变量缺失时跳过，不 mock 通过。
- 静态工具与动态工具至少各有一个 `tools/list` 与 `tools/call` 的契约测试。

## 数据与安全

- 本项目无数据库。`sql/` 目录当前为空。
- `LOCAL_RunShellScript` 等同本地代码执行；相关测试使用临时目录与无害命令。
- 不在文档、日志、测试夹具中出现真实凭据。

## Git 约定

- 仓库已初始化，`main` 跟踪 `origin/main`；提交与推送各自需要用户明确授权。
- 文档与代码分开提交；不提交 `bin/`、`obj/`、本地配置与凭据文件。

## 完成标准

一次文档或实现任务完成，必须同时满足：

1. 需求五件套与总索引一致，`check-docs.mjs` 通过。
2. 新增结论有代码、配置或实测证据；无证据的写入"待确认"。
3. 工具数量、参数默认值、错误码与实施方案一致。
4. 报告写明实际执行过的验证命令与未执行项。
