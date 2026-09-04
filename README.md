# myrouter

本地 HTTP 反向代理，为 OpenAI 兼容客户端（如 VS Code Copilot）增加一层本地鉴权，同时隐藏上游 API Key。

```
客户端 (Copilot / 任意 OpenAI 兼容客户端)
        │  Authorization: Bearer <本地key> 或 x-api-key
        ▼
   myrouter (http://localhost:8080)
        │  本地鉴权校验 + 可选上游密钥替换
        ▼
   上游 (OpenAI / OpenRouter / 任意兼容 API)
```

## 功能特性

- **本地 API Key 鉴权**：客户端用本地 key 访问，支持 `Authorization: Bearer` 和 `x-api-key` 两种头
- **上游密钥隐藏**：配置上游 key 后，客户端 Authorization 会被替换为 `Bearer <上游key>`，真实 key 不暴露给客户端；不配置则原样透传
- **路径智能拼接**：自动处理上游配置 path 与客户端请求 path 的版本段重叠（`/v1` + `/v1/chat/completions` → `/v1/chat/completions`，不会拼成 `/v1/v1/...`）
- **请求全透传**：路径、查询字符串、请求体、自定义头全部透传
- **CORS 支持**：浏览器直连无跨域限制
- **流式转发**：SSE 流式响应（如 `/chat/completions` 的 streaming）原生支持
- **系统托盘**：关窗最小化到托盘，服务继续运行；托盘菜单可直接启停、退出

## 快速开始

### 构建

要求：.NET 10 SDK

```bash
dotnet build myrouter.slnx -c Release
```

### 运行

```bash
# 方式一：直接运行（或双击 bin\Release\net10.0-windows\myrouter.exe）
dotnet run --project myrouter.csproj

# 方式二：单文件发布
dotnet publish myrouter.csproj -c Release -r win-x64 --self-contained
```

首次启动后通过 GUI 配置并保存，配置写入 exe 同目录的 `myrouter.config.json`（**不要提交该文件，内含密钥**）。

### 配置 VS Code Copilot

在 VS Code 的 `settings.json` 中指向本地代理：

```json
{
  "github.copilot.advanced.debug.chat.overrideProxyUrl": "http://localhost:8080",
  "github.copilot.advanced.debug.testOverrideProxyUrl": "http://localhost:8080",
  "github.copilot.advanced.debug.overrideProxyUrl": "http://localhost:8080"
}
```

在 VS Code 里登录 GitHub 时选择"使用代理"，输入本地 key 即可。

## GUI 配置项

| 字段 | 说明 |
|---|---|
| 上游地址 | 目标 API 地址，如 `https://openrouter.ai/api`，可带 path（如 `/v1`） |
| 上游密钥 | 真实上游 key；留空 = 透传客户端 Authorization |
| 端口 | 本地监听端口（默认 8080，1-65535） |
| 启用鉴权 | 校验本地 API Key；关闭后任何请求都放行 |
| 密钥 | 客户端访问本地服务用的 key |
| 记录每个请求 | 在日志区打印每个请求的 URL 与状态码（生产建议关闭） |

## 鉴权细节

- 本地鉴权认两种头：`Authorization: Bearer <key>` 或 `x-api-key: <key>`
- 鉴权失败返回 `401`，日志带诊断信息（头长度 / 是否缺失），**不暴露 key 值**
- 鉴权失败时返回的 `WWW-Authenticate: Bearer` 头

## 测试

```bash
# 冒烟测试（mock 上游，不需网络）：鉴权、透传、路径去重的 11 个场景
dotnet run --project tests/myrouter.SmokeTest

# 真实集成测试（需网络与上游 key）
dotnet run --project tests/myrouter.OpenRouterTest -- <upstream-key> [local-key] [model]
```

## 工具脚本（tools/）

| 脚本 | 用途 |
|---|---|
| `make_icon.py` | PIL 生成应用图标（圆角方块 + M + 箭头，6 尺寸合一），改设计后重跑 |
| `_verify_pe.py` | 验证 exe 内嵌图标与新 ico 逐字节一致（需 `pip install pefile`） |
| `_verify_embed.csx` | 验证 DLL 嵌入资源（窗体/托盘图标）与 ico 一致（需 dotnet-script） |

## License

[MIT](LICENSE)
