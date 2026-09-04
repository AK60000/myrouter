# Project Summary

## Overall Goal
构建并完善 `myrouter`——一个本地 HTTP 反向代理（WinForms GUI），为 VS Code Copilot 等 OpenAI 兼容客户端提供本地 API Key 鉴权，并隐藏上游真实密钥，同时持续打磨其代码质量、项目结构与可维护性。

## Key Knowledge
- **技术栈**：.NET 10 / C#，WinForms（GUI）+ ASP.NET Core Kestrel（代理内核，`ListenLocalhost`），`net10.0-windows`。PIL/Python 生成图标，pefile 验证 PE 资源。
- **核心架构**：`Program.cs`（入口+全局异常处理）、`Forms/MainForm.cs`（GUI+托盘）、`Services/ProxyServer.cs`（代理核心，单 `HttpClient` 复用，30min 超时）、`Models/AppConfig.cs`（JSON 配置：`myrouter.config.json`，存 exe 旁）。中间件：CORS + 鉴权 + `ForwardAsync`（请求体 StreamContent 流式转发，响应流式回传）。
- **鉴权**：本地 Key 认 `Authorization: Bearer` 或 `x-api-key` 两种头（常量 `AppConfig.XApiKeyHeader`）；比对照`ConstantTimeEquals`；配置 `UpstreamApiKey` 时替换客户端 Authorization 为 `Bearer <上游key>`，否则原样透传。
- **路径去重**（`BuildUpstreamUrl`，不区分大小写按段）：① 上游是客户端前缀→用客户端 path；② 版本段重叠（如 `/api/v1` + `/v1/x`）→ 去重叠段；③ 无重叠→直拼。`origin` 和上游 path 段已在 `StartAsync` 预计算出。
- **项目结构（2026-09-04 重组）**：`C:\code\C#\myrouter\` 根有 `myrouter.slnx`（解决方案，统一管理三项目）+ 主项目；`tests\myrouter.SmokeTest\` 和 `tests\myrouter.OpenRouterTest\` 收在 tests/ 下，以 `..\..\myrouter.csproj` 引用主项目；主项目 csproj 有 `DefaultItemExcludes=tests/**`（防嵌套子项目被收集编译）。⚠️ SDK 默认生成 `.slnx`（不是 `.sln`）——这是 .NET 10 的新默认。
- **构建命令**：`dotnet build C:\code\C#\myrouter\myrouter.slnx -c Release`（一次构建全部）。
- **测试命令**：`dotnet run --project C:\code\C#\myrouter\tests\myrouter.SmokeTest -c Release`（mock 冒烟，11 个 case，无网络）；`dotnet run --project C:\code\C#\myrouter\tests\myrouter.OpenRouterTest -- <upstream-key> [local-key] [model]`（真网络集成测试，默认 model=`openai/gpt-4o-mini`，**key 不落代码**）。
- **构建陷阱**：myrouter 运行中会锁 `bin\Release\...\myrouter.exe`，构建报 MSB3026 警告——先退出程序再构建，或确认后 taskkill（上次是 `taskkill /PID 27932 /F`）。
- **⚠️ 非 git 仓库**（`C:\code\C#\` 下无 `.git`）：不能靠 git diff/status/恢复，任何改动都要谨慎，无法回滚。
- **配置默认值**（`AppConfig` 常量）：`DefaultUpstreamUrl="https://api.openai.com"`、`DefaultPort=8080`、`MinPort=1`、`MaxPort=65535`；GUI 默认值引用这些常量。
- **tools/**：仅保留 `make_icon.py`（图标生成器：蓝→青渐变圆角方块+M箭头，6 尺寸 16–256 合一）、`_verify_pe.py`（exe 图标逐字节验证，依赖 pefile）、`_verify_embed.csx`（DLL 嵌入资源验证，用 `dotnet script`）+ `_preview/`（各尺寸 PNG）。

## Recent Actions
- [DONE] **自定义图标**（2026-09-04）：设计"M+箭头"圆角渐变图标并替换三处（exe PE 资源 / 窗体标题栏 / 托盘），6 尺寸合一；验证 exe 内 6 尺寸与新 ico 逐字节 MATCH（pefile），嵌入资源 `myrouter.myrouter.ico` 一致。
- [DONE] **5 处 bug 修复**：`ForwardAsync` 保留 Content-Length（防转发变 chunked）、OCE 分支写 499 前检查 `HasStarted`、`StartAsync` 失败清理 `_cts`、`IsHopByHop` 处理 Connection 头声明的 token（RFC 7230 §6.1）、`LoadAppIcon` 释放资源流。
- [DONE] **用户记忆整理**：新建 `feedback/bobby-work-habits.md`（合并 test-default-state + ask-before-judging + 新观察的协作习惯：计划→执行→验证→迭代、全量审查、二进制级验证严谨度）；生活/时间预设核实内容并入 `bobby-communication-style.md`；用户记忆索引 7→6 条。
- [DONE] **项目记忆同步**：`project/router-overview.md` 更新测试路径/结构、构建坑、非 git 仓库提醒。
- [DONE] **目录重组**：`myrouter.SmokeTest`/`myrouter.OpenRouterTest` 从 `C:\code\C#\` 根搬入 `myrouter\tests\`，创建 `myrouter.slnx` 统一管理；先踩了 CS0579（嵌套 obj 编译）和 MSB9008（引用路径错误）两个坑，已解决。
- [DONE] **代码精简**（简化重构，全部构建 0 警告 + SmokeTest 11/11 通过）：CORS 头收数组 foreach；魔法状态码 → `HttpStatusCode` 枚举 + `StatusClientClosedRequest` 常量；`ForwardAsync` 参数瘦身（3 个固定配置收实例字段）；上游 origin/path 段预计算（去掉每请求重建）；Connection 头解析每请求一次（不再每头一次）；`AppConfig` 提 5 个常量（端口范围、默认值、`x-api-key` 头名）；`Load()` 拍平；两处"显示密码"抽 `WireShowPasswordToggle`；SmokeTest 重写为 `RunCase(proxy, http, name, cfg, test)` 模板（325→245 行），**顺带修了 Case 5 起日志重复订阅的真 bug**。
- [DONE] **清理 tools/**：删除 `_debug_pefile.py`、`_debug_rsrc.py`、`_inspect.py`、`_verify_embed.py`（被 csx 取代的早期版），保留 3 个工具 + 预览目录。

## Current Plan
- [DONE] 图标设计替换 → bug 修复 → 记忆整理 → 目录重组 → 代码精简 → 脚本清理
- [TODO] **OpenRouterTest 尚未跑**：重构后未做真网络集成回归（需要 `<upstream-key>`，网络验证——上次 Carrier 上下文确认运行时已通过端到端，但那是修复前。下次有机会再跑一次确认）。
- [TODO] **软件工程补考**：2026-09-04 提到"明天补考"，涉及 UML 图（用例/类/时序/ER/数据流图等大题）——Bobby 当时在教室复习，会话被建议新开一个再聊。
- [NOTE] 遗留已知边界（检查过决定不改）：`BuildUpstreamUrl` 对含编码特殊字符（`%2F`）的 path 会先解码再拼接，语义可能轻微变化；上游错误时向已断开客户端写 502 响应体可能二次抛异常（仅日志噪音，无崩溃）。

---

## Summary Metadata
**Update time**: 2026-09-04T03:19:43.448Z
