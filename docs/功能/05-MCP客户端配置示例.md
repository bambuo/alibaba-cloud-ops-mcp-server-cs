# 05 MCP客户端配置示例

> 文档编号：05
> 状态：已实现，stdio/HTTP 启动方式本机实测，Docker 与各客户端字段待逐端验证
> 证据基线：本仓库 commit 623b94c；`tools/list` 实测 40/44/7

本文给出可直接粘贴的 MCP 客户端配置。所有 `<>` 内容为占位符，禁止把真实 AccessKey 写入仓库、日志或截图。

## 一、通用说明

| 项 | 说明 |
|---|---|
| 服务名 | 建议统一为 `alibaba-cloud-ops-mcp-server` |
| 传输 | 客户端拉进程用 `stdio`；远程共享用 `streamable-http`（路径 `/mcp`） |
| 凭证 | stdio 只能走环境变量或本机默认凭证链；HTTP 可走请求头 |
| 超时 | 云调用与 OOS 轮询可能超过 60 秒，建议客户端超时设为 600 秒 |
| 日志 | 全部写 stderr；stdio 下 stdout 只输出协议帧，不能加 `echo` 或前台日志 |

三种 `command` 形态，按部署方式选择其一：

| 部署方式 | command | args 前缀 |
|---|---|---|
| 全局安装 dotnet tool | `alibaba-cloud-ops-mcp-server` | 无 |
| `--tool-path` 安装 | `<仓库>/tools/alibaba-cloud-ops-mcp-server` | 无 |
| 源码运行 | `dotnet` | `run --project <仓库>/src/AlibabaCloud.Ops.Mcp.Server --` |
| 已发布目录 | `dotnet` | `<仓库>/publish/AlibabaCloud.Ops.Mcp.Server.dll` |

## 二、stdio：dotnet tool 全局安装

```bash
dotnet tool install --global AlibabaCloud.Ops.Mcp.Server
```

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server": {
      "command": "alibaba-cloud-ops-mcp-server",
      "args": ["--transport", "stdio"],
      "env": {
        "ALIBABA_CLOUD_ACCESS_KEY_ID": "<AccessKeyId>",
        "ALIBABA_CLOUD_ACCESS_KEY_SECRET": "<AccessKeySecret>",
        "ALIBABA_CLOUD_SECURITY_TOKEN": "<可选，STS 临时凭证>"
      },
      "timeout": 600
    }
  }
}
```

## 三、stdio：tool-path 本地安装

```bash
dotnet pack -c Release -o nupkg
dotnet tool install --tool-path ./tools --add-source ./nupkg AlibabaCloud.Ops.Mcp.Server
```

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server": {
      "command": "<仓库绝对路径>/tools/alibaba-cloud-ops-mcp-server",
      "args": ["--transport", "stdio", "--env", "domestic"],
      "env": {
        "ALIBABA_CLOUD_ACCESS_KEY_ID": "<AccessKeyId>",
        "ALIBABA_CLOUD_ACCESS_KEY_SECRET": "<AccessKeySecret>"
      },
      "timeout": 600
    }
  }
}
```

本机 .NET 安装在 `~/.dotnet` 时，apphost 需要能找到运行时，在 `env` 中补：

```json
"DOTNET_ROOT": "/Users/<用户名>/.dotnet"
```

## 四、stdio：源码运行（开发调试）

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server-dev": {
      "command": "dotnet",
      "args": [
        "run",
        "--project", "<仓库绝对路径>/src/AlibabaCloud.Ops.Mcp.Server",
        "--",
        "--transport", "stdio"
      ],
      "env": {
        "ALIBABA_CLOUD_ACCESS_KEY_ID": "<AccessKeyId>",
        "ALIBABA_CLOUD_ACCESS_KEY_SECRET": "<AccessKeySecret>"
      },
      "timeout": 600
    }
  }
}
```

源码运行会在首次调用时编译，启动比 tool 安装慢；客户端超时建议不低于 120 秒。

## 五、HTTP：客户端请求头携带凭证

服务端：

```bash
dotnet run --project src/AlibabaCloud.Ops.Mcp.Server -- \
  --transport streamable-http --host 127.0.0.1 --port 8000
```

客户端：

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server": {
      "url": "http://127.0.0.1:8000/mcp",
      "headers": {
        "x-acs-accesskey-id": "<AccessKeyId>",
        "x-acs-accesskey-secret": "<AccessKeySecret>",
        "x-acs-security-token": "<可选，STS 临时凭证>"
      },
      "timeout": 600
    }
  }
}
```

请求头凭证优先级高于服务端环境变量。若服务端以 `--headers-credential-only` 启动，则只接受请求头，不向环境变量与默认链回退。

## 六、HTTP：服务端环境变量携带凭证

凭证不随请求传输，适合同一台主机上的多客户端共用服务：

```bash
ALIBABA_CLOUD_ACCESS_KEY_ID=<AccessKeyId> \
ALIBABA_CLOUD_ACCESS_KEY_SECRET=<AccessKeySecret> \
dotnet run --project src/AlibabaCloud.Ops.Mcp.Server -- \
  --transport streamable-http --host 127.0.0.1 --port 8000 \
  --headers-credential-only=false
```

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server": {
      "url": "http://127.0.0.1:8000/mcp",
      "timeout": 600
    }
  }
}
```

无请求头凭证时，先读环境变量，再走 `Aliyun.Credentials` 默认链（配置文件、RAM 角色、实例元数据）。

## 七、模式参数示例

### 7.1 代码部署模式（7 个工具）

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server-deploy": {
      "command": "alibaba-cloud-ops-mcp-server",
      "args": ["--transport", "stdio", "--code-deploy"],
      "env": {
        "ALIBABA_CLOUD_ACCESS_KEY_ID": "<AccessKeyId>",
        "ALIBABA_CLOUD_ACCESS_KEY_SECRET": "<AccessKeySecret>"
      },
      "timeout": 900
    }
  }
}
```

部署会构建、上传并轮询 OOS，建议超时设为 900 秒。该模式下 `OOS_CodeDeploy` 要求显式传入 `project_path`。

### 7.2 白名单模式

```json
"args": [
  "--transport", "stdio",
  "--visible-tools", "ECS_DescribeInstances,OOS_RunCommand,LOCAL_ListDirectory"
]
```

名称大小写不敏感；命中静态工具时使用静态版本，例如白名单中的 `ECS_DescribeInstances` 是部署模块的两参数版本（`instance_ids`、`region_id`），不是 37 参数的动态版本。

### 7.3 通用工具与额外 API

```json
"args": [
  "--transport", "stdio",
  "--services", "ecs,vpc,rds",
  "--extra-config", "{\"sls\":[\"GetProject\",\"ListProject\"]}"
]
```

`--services` 额外注册 4 个通用工具（44 个工具）；`--extra-config` 只支持 JSON 字符串，不支持 Python 单引号字典。

### 7.4 国际站

```json
"args": ["--transport", "stdio", "--env", "international"]
```

国际站会切换 `bssopenapi` 计费端点；与账号站点不匹配时云调用会失败。

## 八、Docker

镜像默认以 Streamable HTTP 启动：

```bash
docker build -t alibaba-cloud-ops-mcp-server:0.9.27 .
docker run -d --name ops-mcp -p 8000:8000 \
  -e ALIBABA_CLOUD_ACCESS_KEY_ID=<AccessKeyId> \
  -e ALIBABA_CLOUD_ACCESS_KEY_SECRET=<AccessKeySecret> \
  alibaba-cloud-ops-mcp-server:0.9.27
```

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server": {
      "url": "http://127.0.0.1:8000/mcp",
      "timeout": 600
    }
  }
}
```

容器内用 stdio 需覆盖入口并保持标准输入打开：

```json
{
  "mcpServers": {
    "alibaba-cloud-ops-mcp-server-docker": {
      "command": "docker",
      "args": [
        "run", "-i", "--rm",
        "--entrypoint", "dotnet",
        "-e", "ALIBABA_CLOUD_ACCESS_KEY_ID",
        "-e", "ALIBABA_CLOUD_ACCESS_KEY_SECRET",
        "alibaba-cloud-ops-mcp-server:0.9.27",
        "AlibabaCloud.Ops.Mcp.Server.dll", "--transport", "stdio"
      ],
      "env": {
        "ALIBABA_CLOUD_ACCESS_KEY_ID": "<AccessKeyId>",
        "ALIBABA_CLOUD_ACCESS_KEY_SECRET": "<AccessKeySecret>"
      },
      "timeout": 600
    }
  }
}
```

`docker run` 的 `-e 变量名` 不写值表示从客户端进程环境透传，避免在配置中重复写明文。

## 九、不同客户端的字段差异

| 客户端 | 顶层键 | stdio 字段 | HTTP 字段 |
|---|---|---|---|
| Qoder、Cline | `mcpServers` | `command`、`args`、`env` | `url`、`headers` |
| Claude Desktop | `mcpServers` | `command`、`args`、`env` | 不支持远程 HTTP |
| VS Code | `servers` | `type: "stdio"`、`command`、`args`、`env` | `type: "http"`、`url`、`headers` |

以客户端当前版本文档为准；字段名不同时只调整外层结构，`command`、`args`、`env`、`url`、`headers` 的内容不变。

## 十、验证与排障

启动后先确认工具面：

| 启动参数 | 期望 `tools/list` 数量 |
|---|---|
| 默认 | 40 |
| `--services ecs,vpc,rds` | 44 |
| `--code-deploy` | 7 |
| 白名单 3 项 | 3 |
| `--extra-config` 增加 1 个 API | 41 |

常见问题：

1. 客户端一直停在连接中：检查 `command` 是否为绝对路径、`DOTNET_ROOT` 是否正确、stderr 是否有运行时错误。
2. 工具数少于预期：动态工具依赖 `api.aliyun.com` 元数据，离线或代理失败时只跳过该工具；检查 stderr 的 `Dynamic tools registered` 数量。
3. 调用返回 `MissingCredentials`：stdio 检查 `env` 是否注入；HTTP 检查请求头名称是否为 `x-acs-accesskey-id` 与 `x-acs-accesskey-secret`。
4. 调用返回 `InvalidServiceName`：服务码拼写错误，先用 `ListAPIs` 验证（需 `--services`）。
5. HTTP 返回 404：路径必须是 `/mcp`；服务端需以 `--transport streamable-http` 启动。
6. 中文乱码：客户端与容器统一 UTF-8；不要把日志写入 stdout。

安全提示：`LOCAL_RunShellScript` 等同本地命令执行；HTTP 模式默认绑定 `127.0.0.1`，对外暴露前必须增加鉴权、TLS 与网络隔离。
