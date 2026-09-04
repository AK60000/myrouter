# myrouter

本地 HTTP 反向代理，为 OpenAI 兼容客户端提供本地鉴权与上游密钥隐藏。

**解决的问题**：客户端（如 VS Code 中的 AI 助手、脚本、任何 OpenAI 兼容工具）需要访问上游 API 时，直接暴露上游 key 有风险；直接暴露 API 端点又无法控制访问。myrouter 在本地起一个代理层，客户端只认识本地 key，真实上游 key 只存在于 myrouter 的配置里，永不下发。

```
┌──────────────┐   Authorization: Bearer <本地key>   ┌──────────────┐  真实上游key   ┌─────────────────┐
│ 任意客户端     │ ─────────────────────────────────▶ │   myrouter    │ ────────────▶ │  上游 API        │
│ (OpenAI 兼容) │   或 x-api-key: <本地key>           │  localhost:8080│              │ (OpenAI/OpenRouter) │
└──────────────┘                                    └──────────────┘              └─────────────────┘
                                                      ▲ 本地鉴权 + 可选上游密钥替换
```

## 功能特性

- **本地 API Key 鉴权**：客户端用本地 key 访问，支持 `Authorization: Bearer` 与 `x-api-key` 两种头；鉴权失败返回 401，日志附诊断信息但不泄露 key 值
- **上游密钥隐藏**：配置上游 key 后，转发时把客户端 Authorization 替换为 `Bearer <上游key>`，真实 key 不离开本机；不配置则原样透传
- **路径智能拼接**：自动处理上游配置 path 与客户端请求 path 的重叠，不会出现 `/v1/v1/...` 双重路径
- **完整透传**：路径、查询字符串、请求体、自定义头全部转发
- **流式响应**：SSE 流式（streaming）原生支持，边收边转
- **CORS 支持**：浏览器直连无跨域限制，含预检（OPTIONS）处理
- **系统托盘**：关窗最小化到托盘，服务后台继续运行；托盘菜单支持显示窗口 / 启动 / 停止 / 退出

## 快速开始

### 环境要求

- .NET 10 SDK（构建）/ .NET 10 Desktop Runtime（运行）
- Windows 10/11（依赖 WinForms）

### 构建

```bash
git clone https://github.com/AK60000/myrouter.git
cd myrouter
dotnet build myrouter.slnx -c Release
```

### 运行

```bash
# 直接运行（或双击 bin\Release\net10.0-windows\myrouter.exe）
dotnet run --project myrouter.csproj

# 单文件自包含发布（无需装 .NET 运行时；自动裁剪，约 35MB）
dotnet publish myrouter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### 配置

首次启动后，在 GUI 中填写配置并点击"💾 保存配置"，写入 exe 同目录的 `myrouter.config.json`。

> ⚠️ `myrouter.config.json` 内含密钥，**不要提交到代码仓库**（已在 .gitignore 中排除）。

## 配置项

| 字段 | 说明 | 示例 |
|---|---|---|
| 上游地址 | 目标 API 的 base URL，可带 path | `https://openrouter.ai/api`、`https://api.openai.com/v1` |
| 上游密钥 | 真实上游 key；留空 = 透传客户端 Authorization | `sk-or-v1-...` |
| 端口 | 本地监听端口（默认 8080，1-65535） | `8080` |
| 超时 (秒) | 上游请求超时（默认 1800，1-86400；SSE 长连接可调大） | `1800` |
| 启用鉴权 | 关闭后任何请求都放行（仅内网调试用） | 勾选 |
| 密钥 | 客户端访问本地服务所用的 key | `my-local-key` |
| 记录每个请求 | 在日志区打印请求 URL 与状态码（生产建议关闭） | 不勾选 |

### 客户端接入

任意 OpenAI 兼容客户端，把 base URL 指向 `http://localhost:8080`，API key 填本地 key 即可。例如：

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer my-local-key" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'
```

## 鉴权细节

- 本地鉴权认两种头，任一即可：`Authorization: Bearer <key>`、`x-api-key: <key>`
- 鉴权失败返回 `401` + `WWW-Authenticate: Bearer`，日志显示诊断（"Authorization 头长度 N" / "x-api-key 头长度 N" / "两个鉴权头都没有"）——用于排查客户端 header 格式，**不暴露 key 值**
- 上游密钥替换只影响转发出去的 Authorization 头（替换为 `Bearer <上游key>`）；自定义头原样透传
- **配置了上游密钥时，本地鉴权用的 `x-api-key` 头会被剥离，不会转发给上游**（防止本地 key 泄露）；未配置上游密钥的透传模式下才原样透传

## 路径拼接规则

上游配置的 path 与客户端请求 path 按段（不区分大小写）去重：

| 上游配置 | 客户端请求 | 实际转发 | 规则 |
|---|---|---|---|
| `/v1` | `/v1/chat/completions` | `/v1/chat/completions` | 前缀重叠 → 用客户端 path |
| `/api/v1` | `/v1/chat/completions` | `/api/v1/chat/completions` | 版本段重叠 → 上游 + 客户端去重 |
| `/api/v2` | `/v1/chat/completions` | `/api/v2/v1/chat/completions` | 无重叠 → 保守直拼 |

## 项目结构

```
myrouter/
├── Program.cs                  # 入口：异常兜底 + 启动主窗体
├── Forms/MainForm.cs           # GUI、托盘、配置持久化
├── Models/AppConfig.cs         # 配置模型与 JSON 读写
├── Services/ProxyServer.cs     # Kestrel 代理核心：鉴权 + 转发 + 路径拼接
├── tools/                      # 图标生成 / 验证脚本
│   ├── make_icon.py            #   PIL 生成应用图标
│   ├── _verify_pe.py           #   验证 exe 内嵌图标
│   └── _verify_embed.csx       #   验证 DLL 嵌入资源
└── tests/
    ├── myrouter.SmokeTest/     # mock 冒烟测试（不需网络）
    └── myrouter.OpenRouterTest # 真实集成测试（需上游 key）
```

## 开发与测试

```bash
# 一次构建全部三个项目
dotnet build myrouter.slnx -c Release

# 冒烟测试（mock 上游，不需网络）：鉴权、透传、路径去重、gzip 透传、超时等 14 个场景
dotnet run --project tests/myrouter.SmokeTest

# 真实集成测试（需网络与上游 key）
dotnet run --project tests/myrouter.OpenRouterTest -- <upstream-key> [local-key] [model]
```

## 技术栈

- C# / .NET 10
- WinForms（GUI、托盘、NotifyIcon）
- ASP.NET Core Kestrel（HTTP 代理内核）
- System.Net.Http（上游转发）
- PIL（图标生成，仅开发期 tools 使用）

## 许可证

[MIT License](LICENSE)，Copyright © 2026 AK60000
