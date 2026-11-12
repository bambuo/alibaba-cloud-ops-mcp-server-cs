# 0927 CSharp重构云运维MCP服务 — 前端对接文档

> 对接方：MCP 客户端（Qoder / Cursor / Cline 等）
> 日期：2026-11-12 ｜ 状态：设计稿，待评审

> 说明：本需求无 Web 前端。本文档按「前端对接文档」的六节骨架编写 MCP 客户端对接契约；40 个工具的参数 schema 由服务端 `tools/list` 动态返回，不在本文档重复枚举。

## 一、接口与权限

### 1.1 连接方式

| 传输 | 启动方式 | 地址 |
|---|---|---|
| stdio | 客户端拉起子进程 | 无网络地址 |
| Streamable HTTP | 服务端监听 | `http://{host}:{port}/mcp`，默认 `127.0.0.1:8000` |

服务端不内置登录与鉴权。HTTP 模式的访问控制由部署方通过网络隔离、反向代理或网关完成。

### 1.2 凭证

| 方式 | 适用传输 | 说明 |
|---|---|---|
| 进程环境变量 | stdio / HTTP | `ALIBABA_CLOUD_ACCESS_KEY_ID`、`ALIBABA_CLOUD_ACCESS_KEY_SECRET`、可选 `ALIBABA_CLOUD_SECURITY_TOKEN` |
| 请求头 | 仅 HTTP | `x-acs-accesskey-id`、`x-acs-accesskey-secret`、`x-acs-security-token` |

请求头凭证优先于环境变量。服务以 `--headers-credential-only` 启动时，缺少请求头将直接报错。

### 1.3 权限范围

服务不区分角色，工具能否调用完全取决于 AK 所拥有的 RAM 权限。建议按工具面拆分子账号：只读查询、批量运维、部署三类分别授权。

## 二、字段说明

### 2.1 工具描述字段（`tools/list` 单项）

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 工具名，格式为 `{服务码}_{API}`，如 `ECS_DescribeInstances` |
| `description` | string | 工具用途，中文 |
| `inputSchema` | object | JSON Schema，`type` 固定 `object` |
| `inputSchema.properties` | object | 参数名到参数定义的映射 |
| `inputSchema.properties.*.type` | string | `string` / `integer` / `boolean` / `array` / `object` / `number` |
| `inputSchema.properties.*.description` | string | 参数说明，包含参数类型与示例 |
| `inputSchema.properties.*.default` | any | 可选，未传时服务端使用该值 |
| `inputSchema.required` | string[] | 必填参数名列表 |

### 2.2 调用结果字段（`tools/call`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `content` | array | 结果块列表 |
| `content[].type` | string | 固定 `text` |
| `content[].text` | string | 业务结果的 JSON 字符串 |
| `isError` | boolean | 为 `true` 表示工具执行失败；可能缺省，缺省视为 `false` |

### 2.3 业务结果字段

| 工具组 | 顶层结构 | 说明 |
|---|---|---|
| ECS / VPC / RDS 动态工具 | 阿里云响应 JSON | 字段名与 OpenAPI 返回一致 |
| OOS 工具 | 执行列表 JSON | 含 `executions[].status`、`executions[].statusMessage` |
| CMS 工具 | 指标数据点数组 | 每项含时间戳与值 |
| OSS 工具 | 结构化对象 | 含状态码、ETag、对象列表等，C# 版为 JSON，非上游 Python 字符串 |
| LOCAL 工具 | 固定字段对象 | 见第三节对应示例 |
| 部署工具 | 部署结果对象 | 含执行 ID、状态、控制台链接 |

## 三、接口明细与示例

### 3.1 tools/list

**请求参数**

无。请求体为 MCP `tools/list` 标准结构，可分页。

**请求示例**

```json
{ "jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {} }
```

**响应字段表**

| 字段 | 类型 | 说明 |
|---|---|---|
| `result.tools` | array | 工具定义列表 |
| `result.tools[].name` | string | 工具名 |
| `result.tools[].description` | string | 工具描述 |
| `result.tools[].inputSchema` | object | 输入 JSON Schema |
| `result.nextCursor` | string 或 null | 下一页游标 |

**响应示例**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [
      {
        "name": "ECS_DescribeInstances",
        "description": "查询一台或多台ECS实例的详细信息",
        "inputSchema": {
          "type": "object",
          "properties": {
            "RegionId": { "type": "string", "description": "地域ID", "default": "cn-hangzhou" },
            "InstanceIds": { "type": "array", "description": "实例ID列表" }
          },
          "required": []
        }
      }
    ],
    "nextCursor": null
  }
}
```

### 3.2 tools/call

**请求参数**

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 是 | 工具名，必须与 `tools/list` 返回一致 |
| `arguments` | object | 否 | 参数键值对，键名与 `inputSchema` 一致 |

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "ECS_DescribeInstances",
    "arguments": { "RegionId": "cn-hangzhou", "MaxResults": 10 }
  }
}
```

**响应字段表**

| 字段 | 类型 | 说明 |
|---|---|---|
| `result.content` | array | 结果块 |
| `result.content[].type` | string | `text` |
| `result.content[].text` | string | JSON 字符串，需二次解析 |
| `result.isError` | boolean | 失败标记 |
| `error.code` | number | 协议层错误码，仅协议异常出现 |
| `error.message` | string | 协议层错误描述 |

**响应示例（成功）**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"TotalCount\":1,\"Instances\":[{\"InstanceId\":\"i-bp1example\",\"Status\":\"Running\"}]}"
      }
    ],
    "isError": false
  }
}
```

**响应示例（业务失败）**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [{ "type": "text", "text": "InvalidParameter: RegionId is required" }],
    "isError": true
  }
}
```

### 3.3 Streamable HTTP 传输

**请求参数**

| 项 | 值 | 说明 |
|---|---|---|
| Method | POST | 发送 JSON-RPC 消息 |
| Path | `/mcp` | 固定路径 |
| Content-Type | `application/json` | 不支持表单 |
| Accept | `application/json, text/event-stream` | 服务端可能以 SSE 流返回 |
| 自定义头 | `x-acs-accesskey-id` 等 | 可选，用于请求头凭证 |

**请求示例**

```http
POST /mcp HTTP/1.1
Host: 127.0.0.1:8000
Content-Type: application/json
Accept: application/json, text/event-stream
x-acs-accesskey-id: <AK_ID>
x-acs-accesskey-secret: <AK_SECRET>

{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}
```

**响应字段表**

| 字段 | 类型 | 说明 |
|---|---|---|
| HTTP 状态码 | number | 200 成功；400 请求非法；401 凭证缺失；405 方法不允许 |
| `Mcp-Session-Id` | string | 无状态模式下不返回 |
| Body | object 或 SSE 流 | JSON-RPC 响应 |

**响应示例**

```http
HTTP/1.1 200 OK
Content-Type: application/json

{"jsonrpc":"2.0","id":3,"result":{"tools":[],"nextCursor":null}}
```

## 四、错误文案

| 场景 | `isError` | 文案（前缀固定，便于客户端匹配） |
|---|---|---|
| 缺少必填参数 | true | `InvalidParameter: {参数名} is required` |
| 参数类型错误 | true | `InvalidParameter: {参数名} expects {类型}` |
| 凭证缺失 | true | `MissingCredentials: no AccessKey found in headers or environment` |
| 凭证仅允许请求头 | true | `MissingCredentials: headers-credential-only is enabled` |
| 未知工具 | true | `UnknownTool: {工具名}` |
| 阿里云拒绝 | true | `{Code}: {Message}` |
| 本地命令超时 | true | `CommandTimeout: exceeded {秒} seconds` |
| 本地命令不存在 | true | `CommandNotFound: {命令}` |
| 文件或目录不存在 | true | `NotFound: {路径}` |
| 路径不是目录 | true | `InvalidPath: {路径} is not a directory` |
| 部署失败 | true | `DeployFailed: {statusMessage}` |
| 传输不支持 | 进程退出 | `UnsupportedTransport: sse is not supported` |
| 参数 JSON 非法 | 进程退出 | `InvalidArgument: --extra-config expects a JSON object` |

## 五、注意事项

1. 工具结果中的 `text` 是 JSON 字符串，需要二次 `JSON.parse`。
2. 动态工具在启动时由元数据生成；元数据接口不可达时该类工具不会出现在 `tools/list`，静态工具不受影响。
3. 工具命名大小写敏感；`--visible-tools` 匹配不敏感。
4. `LOCAL_RunShellScript` 会在服务端主机执行任意命令，客户端必须向用户明示风险。
5. 破坏性工具（删除实例、删除 Bucket、重置密码、更换系统盘、批量执行命令）建议在客户端开启二次确认。
6. 部署工具耗时较长，客户端超时建议不低于 600 秒，并支持取消。
7. OSS 与 CMS 的返回结构是 C# 版重新定义的 JSON，与上游 Python 字符串不同；按字段消费，不要按字符串匹配。
8. HTTP 模式无内置鉴权，不要直接暴露到公网。
9. 请求头凭证不会被日志记录；客户端也不应在 URL、查询参数中传递凭证。

## 六、更新历史

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-11-12 | v1 | 初版，定义 MCP 传输、`tools/list`、`tools/call` 与错误文案 |