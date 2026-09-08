// 前端核心逻辑验证：用最小 DOM stub 执行 index.html 的真实 <script>，
// 验证思维链提取、renderContent 返回值、finishStream 光标/占位清理、用户画像面板行为。
// 用法: node tools/_verify_frontend.mjs
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const html = readFileSync(join(root, "content", "index.html"), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
if (!script) { console.error("[FAIL] 未找到 <script> 块"); process.exit(1); }
// 第三方依赖（markdown-it + highlight.js 子集）：注入同一作用域供 mdToHtml 测试（与浏览器加载路径一致）
const vendorBundle = readFileSync(join(root, "tools", "vendor", "vendor.bundle.js"), "utf8");

// ── 最小 DOM stub：getElementById 按 id 缓存（同一 id 同一实例），addEventListener 记录事件可回放 ──
function makeEl(tag) {
  const el = {
    tagName: tag, className: "", _html: "", textContent: "",
    children: [], style: {}, value: "", checked: false, disabled: false, files: [], hidden: false,
    _handlers: {},
    set innerHTML(v) { this._html = String(v); this.children = []; },
    get innerHTML() { return this._html; },
    appendChild(c) { this.children.push(c); c.parentNode = this; return c; },
    prepend(c) { this.children.unshift(c); c.parentNode = this; },
    append(...cs) { cs.forEach(c => this.appendChild(c)); },
    querySelector() { return makeEl("div"); },
    querySelectorAll() { return []; },
    addEventListener(ev, fn) { this._handlers[ev] = fn; },
    remove() {},
    focus() {},
    scrollIntoView() {},
  };
  el.classList = {
    add(c) { el.className = (el.className + " " + c).trim(); },
    remove(c) { el.className = el.className.replace(new RegExp("\\b" + c + "\\b", "g"), "").trim(); },
    contains(c) { return el.className.includes(c); },
    toggle(c) {
      if (el.className.includes(c)) { el.className = el.className.replace(new RegExp("\\b" + c + "\\b", "g"), "").trim(); return false; }
      el.className = (el.className + " " + c).trim(); return true;
    },
  };
  return el;
}
const els = {};
const doc = { getElementById: id => els[id] ??= makeEl("div"), createElement: t => makeEl(t) };
const ls = { getItem: () => null, setItem() {} };
// 可编程 fetch：测试里设置 fetchMock.handler 按 URL 分发响应；未设置时一律拒绝
// （顶层 loadModels/convInit 等在测试前就跑完，走拒绝路径）
const fetchMock = { handler: null };
const fetchFn = (url, opts) => fetchMock.handler
  ? fetchMock.handler(url, opts)
  : Promise.reject(new Error("fetch unavailable"));
// 测试夹具：真实 SSE 流（与浏览器解析路径一致）与 JSON 响应构造器
const sseBody = s => new ReadableStream({
  start(c) { c.enqueue(new TextEncoder().encode(s)); c.close(); },
});
const jsonRes = j => ({ ok: true, json: async () => j });
const fixtures = {
  // 工具调用 SSE：arguments 分两片（验证增量拼接）→ 第二次请求返回纯文本最终回答
  sseTool:
    'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"run_javascript","arguments":"{\\"code\\":\\""}}]}}]}\n\n' +
    'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"console.log(1+1)\\""}}]}}]}\n\n' +
    "data: [DONE]\n\n",
  ssePlain: 'data: {"choices":[{"delta":{"content":"你好"}}]}\n\n' + "data: [DONE]\n\n",
};

let failed = 0;
const assert = (cond, msg) => {
  console.log((cond ? "[OK] " : "[FAIL] ") + msg);
  if (!cond) failed = 1;
};

// 测试代码追加进 script 作用域，直接访问模块级 let 变量；
// 命名为 runTests 并由外层 return，让顶层 await 真正等到全部断言完成
const tests = `
async function runTests() {
// 1. splitThinking：<think> 与 <thinking> 均提取
let st = splitThinking("<think>先检查边界</think>你");
assert(st.text === "你" && st.thinking === "先检查边界", "splitThinking extracts <think>");
st = splitThinking("正文<thinking>推理</thinking>更多");
assert(st.text === "正文更多" && st.thinking === "推理", "splitThinking extracts <thinking>");
st = splitThinking("未闭合<think>还在想");
assert(st.text === "未闭合" && st.thinking === "还在想", "splitThinking tolerates unclosed tag");

// 2. renderContent：返回剔除思维链的干净正文；光标只在 withCursor 时追加
liveBubble = makeEl("div");
const clean = renderContent("<thinking>t</thinking>body", "", false);
assert(clean === "body", "renderContent returns clean text");
assert(!contentNode.innerHTML.includes("cursor"), "no cursor span when withCursor=false");
const clean2 = renderContent("x", "", true);
assert(clean2 === "x", "renderContent plain text");
assert(contentNode.innerHTML.includes("cursor"), "cursor span appended when withCursor=true");

// 3. finishStream（有正文）：移除内容里的光标，气泡保留渲染结果
let cursorRemoved = false;
contentNode = { querySelector: () => ({ remove() { cursorRemoved = true; } }) };
liveBubble.innerHTML = "<div class=prose>x<span class=cursor></span></div>";
const bubbleHtml = liveBubble.innerHTML;
finishStream();
assert(cursorRemoved, "finishStream removes cursor inside contentNode");
assert(bubbleHtml.includes("<div"), "finishStream keeps rendered content");
assert(liveBubble === null && thinkBox === null && contentNode === null, "finishStream nulls refs");

// 4. finishStream（空回复）：无 contentNode 时清空占位，不留闪烁残留
const emptyBubble = makeEl("div");
emptyBubble.innerHTML = "<div class=prose></div>";
liveBubble = emptyBubble;
contentNode = null; thinkBox = null;
finishStream();
assert(emptyBubble.innerHTML === "", "finishStream clears empty bubble placeholder");

// 5. finishStream（错误气泡）：err 类存在时保留错误文案
const errBubble = makeEl("div");
errBubble.classList = { add() {}, remove() {}, contains: () => true };
errBubble.textContent = "出错了：boom";
liveBubble = errBubble;
contentNode = null; thinkBox = null;
finishStream();
assert(errBubble.textContent === "出错了：boom", "finishStream preserves error message");

// 6. 用户画像面板：打开、空会话拦截、请求失败兜底提示
agentOverlay.hidden = true;
$("btnAgent")._handlers.click();
assert(!agentOverlay.hidden, "btnAgent opens agent panel");
$("btnAgentGen")._handlers.click();
assert(agentNote.textContent.includes("先聊几句"), "AI generate blocked when conversation is empty");
await agentPost({ content: "x" }).catch(e => agentNoteShow("保存失败：" + e.message, "err"));
assert(agentNote.textContent.includes("保存失败"), "agentPost failure shows save error note");

// 7. 会话管理：渲染列表/标记激活/失败兜底/内容归一
conversations = [{ id: "a", title: "第一条" }, { id: "b", title: "" }];
activeId = "a";
convRender();
assert(convListBox.children.length === 2, "convRender lists conversations");
assert(convListBox.children[0].className.includes("active"), "convRender marks active conversation");
await convSaveCurrent();   // fetch 失败应静默，不改变状态
assert(activeId === "a", "convSaveCurrent keeps state on failure");
const parts = normParts("hello");
assert(parts.length === 1 && parts[0].type === "text" && parts[0].text === "hello", "normParts wraps string content");

// 8. JS 代码运行（浏览器原生 JS）：渲染按钮/核心执行/对象格式化/运行时错误
//（死循环中断由 Web Worker + 超时 terminate 保证，浏览器机制，node 环境不模拟）
const jsHtml = mdToHtml("正文\\n\`\`\`js\\nconsole.log(1+1)\\n\`\`\`");
assert(jsHtml.includes("runCode"), "mdToHtml renders js block with run button");
const r1 = codeRunCore("console.log(1+1, 'hi')");
assert(r1.ok && r1.lines[0] === "2 hi", "codeRunCore captures console.log: " + JSON.stringify(r1));
const r2 = codeRunCore("console.log({a: 1})");
assert(r2.ok && r2.lines[0] === '{"a":1}', "codeRunCore formats objects: " + JSON.stringify(r2));
const r3 = codeRunCore("null.x");
assert(!r3.ok && r3.error, "codeRunCore surfaces runtime error: " + JSON.stringify(r3));

// 8.5 markdown-it：表格/嵌套列表/XSS 转义/危险链接拦截/新窗口/代码高亮
const tblHtml = mdToHtml("| a | b |\\n|---|---|\\n| 1 | 2 |");
assert(tblHtml.includes("<table>") && tblHtml.includes("<th>a</th>"), "mdToHtml renders tables");
const nested = mdToHtml("- 1\\n  - 1.1\\n    - 1.1.1");
assert(nested.includes("<ul>") && nested.includes("<li>1.1.1"), "mdToHtml renders nested lists");
const xssHtml = mdToHtml("<script>alert(1)</script>");
assert(!xssHtml.includes("<script>") && xssHtml.includes("&lt;script&gt;"), "mdToHtml escapes raw HTML");
const jsLink = mdToHtml("[x](javascript:alert(1))");
assert(!jsLink.includes("<a") && !jsLink.includes("href="), "mdToHtml blocks javascript: links");
const linkHtml = mdToHtml("[文档](https://example.com)");
assert(linkHtml.includes('target="_blank"') && linkHtml.includes('rel="noopener"'), "mdToHtml opens links in new tab");
const hlHtml = mdToHtml("\\\`\\\`\\\`js\\nconst x = 1;\\n\\\`\\\`\\\`");
assert(hlHtml.includes("hljs-keyword"), "mdToHtml highlights js code");

// 8.6 KaTeX：公式渲染与 auto-render 导出（DOM 遍历由 renderMath try/catch 兜底，stub 不模拟）
const khtml = myvendor.katex.renderToString("x^2", { throwOnError: false });
assert(khtml.includes("katex") && khtml.includes("<span"), "katex renders latex to HTML");
const kfrac = myvendor.katex.renderToString("\\\\frac{1}{2}", { throwOnError: false });
assert(kfrac.includes("frac"), "katex renders \\frac");
assert(typeof myvendor.renderMathInElement === "function", "auto-render exposed on vendor bundle");

// 9. runCode 返回结构：{ text（含耗时展示）, output（纯结果，供工具回填） }
assert(autoRun.checked === true, "auto-run enabled by default");
const origRunCode = runCode;
runCode = async (code, container) => ({ text: "2\\n耗时 1 ms", output: "2" });
const rcRes = await runCode("console.log(1+1)", makeEl("div"));
assert(rcRes.output === "2" && rcRes.text.includes("耗时"), "runCode returns { text, output }");
runCode = origRunCode;

// 10. tool calling 两段式：AI 发起 run_javascript → 执行回填 tool 消息 → 基于真实输出最终回答
activeId = "conv-test";
conversations = [];
messages = [];
$("wrap").innerHTML = "";
$("input").value = "算一下 1+1";
let calls = [];
fetchMock.handler = (url, opts) => {
  const u = String(url);
  calls.push({ url: u, body: opts && opts.body ? JSON.parse(opts.body) : null });
  if (u.endsWith("/chat")) {
    const first = calls.filter(c => c.url.endsWith("/chat")).length === 1;
    return Promise.resolve({ ok: true, body: sseBody(first ? fixtures.sseTool : fixtures.ssePlain) });
  }
  if (u.endsWith("/title")) return Promise.resolve(jsonRes({ ok: true, title: "T" }));
  if (u.endsWith("/conversations")) return Promise.resolve(jsonRes({ items: [] }));
  return Promise.resolve(jsonRes({ ok: true }));
};
runCode = async (code, container) => ({ text: "2\\n耗时 0 ms", output: "2" });
await send();
runCode = origRunCode;
const chatCalls = calls.filter(c => c.url.endsWith("/chat"));
assert(chatCalls.length === 2, "tool loop sends second /chat after run_javascript");
assert(chatCalls[0].body.tools?.[0]?.function?.name === "run_javascript", "request declares run_javascript tool");
assert(chatCalls[0].body.tool_choice === "auto", "tool_choice auto when auto-run enabled");
const asstTool = messages.find(m => m.role === "assistant" && Array.isArray(m.tool_calls));
assert(!!asstTool && asstTool.tool_calls[0].function.arguments.includes("console.log"),
  "assistant message persists tool_calls with joined arguments");
const toolMsg = messages.find(m => m.role === "tool");
assert(!!toolMsg && toolMsg.tool_call_id === "call_1" && toolMsg.content === "2", "tool result message carries real output");
assert(messages[messages.length - 1].role === "assistant" && messages[messages.length - 1].content === "你好",
  "final answer given after tool result");
assert(chatCalls[1].body.messages.some(m => m.role === "tool" && m.content === "2"), "second request carries tool result");
assert($("wrap").children.filter(c => c.className.includes("ai")).length === 1,
  "tool loop renders one merged ai bubble (steps + final answer)");

// 11. send() 无工具调用 → 只发一次 /chat
calls = [];
fetchMock.handler = (url, opts) => {
  const u = String(url);
  calls.push({ url: u });
  if (u.endsWith("/chat")) return Promise.resolve({ ok: true, body: sseBody(fixtures.ssePlain) });
  if (u.endsWith("/title")) return Promise.resolve(jsonRes({ ok: true, title: "T" }));
  if (u.endsWith("/conversations")) return Promise.resolve(jsonRes({ items: [] }));
  return Promise.resolve(jsonRes({ ok: true }));
};
messages = [];
$("wrap").innerHTML = "";   // 清掉上一测试残留的气泡
$("input").value = "你好";
await send();
assert(calls.filter(c => c.url.endsWith("/chat")).length === 1, "no second /chat when AI answers without tool");

// 12. summarizeTitle：剥离图片/文件附件，只发文本消息
const titleBodies = [];
fetchMock.handler = (url, opts) => {
  const u = String(url);
  if (u.endsWith("/title")) titleBodies.push(JSON.parse(opts.body));
  return Promise.resolve(jsonRes({ ok: true }));
};
messages = [
  { role: "user", content: [{ type: "text", text: "帮我看看" }, { type: "image_url", image_url: { url: "data:image/png;base64,AAAA" } }] },
  { role: "assistant", content: "好的" },
];
activeId = "conv-t";
await summarizeTitle();
assert(titleBodies.length === 1 && titleBodies[0].messages.length === 2, "summarizeTitle sends messages");
assert(!JSON.stringify(titleBodies[0]).includes("base64"), "summarizeTitle strips attachments");

// 13. finishStream 空引用防护：liveBubble 为 null 时不抛
liveBubble = null; contentNode = null; thinkBox = null;
finishStream();
assert(liveBubble === null, "finishStream tolerates null liveBubble");

// 14. convRenderBody：代码运行输出渲染为 sys 提示样式（非普通用户气泡）
messages = [
  { role: "user", content: "代码运行输出：\\n2\\n请基于实际输出回答。" },
  { role: "assistant", content: "结果是 2" },
];
activeId = "conv-t";
$("wrap").innerHTML = "";
convRenderBody();
const sysMsg = $("wrap").children.find(c => c.className.includes("sys"));
assert(!!sysMsg, "convRenderBody renders result message as sys note");

// 14.5 convRenderBody：工具步骤、输出与最终回答合并在一个气泡里（与实时流一致）
messages = [
  { role: "user", content: "算一下" },
  { role: "assistant", content: null, tool_calls: [{ id: "c1", type: "function", function: { name: "run_javascript", arguments: '{"code":"console.log(1)"}' } }] },
  { role: "tool", tool_call_id: "c1", content: "1" },
  { role: "assistant", content: "结果是 1" },
];
activeId = "conv-t";
$("wrap").innerHTML = "";
convRenderBody();
const aiMsgs = $("wrap").children.filter(c => c.className.includes("ai"));
assert(aiMsgs.length === 1, "tool round + final answer merged into one bubble");
const mergedHtml = aiMsgs[0].innerHTML;
assert(mergedHtml.includes("toolCall") && mergedHtml.includes("console.log(1)"),
  "convRenderBody renders tool_calls as code step block");
assert(mergedHtml.includes('codeOut">1<'), "tool output merged into the step block");
assert(mergedHtml.includes("结果是 1"), "final answer merged into the same bubble");

// 15. titlePending：首轮请求失败（标题未总结）→ 下一轮成功时补总结
calls = [];
let chatFail = true;
fetchMock.handler = (url, opts) => {
  const u = String(url);
  calls.push({ url: u });
  if (u.endsWith("/chat")) {
    if (chatFail) return Promise.reject(new Error("upstream down"));
    return Promise.resolve({ ok: true, body: sseBody(fixtures.ssePlain) });
  }
  if (u.endsWith("/title")) return Promise.resolve(jsonRes({ ok: true, title: "T" }));
  if (u.endsWith("/conversations")) return Promise.resolve(jsonRes({ items: [] }));
  return Promise.resolve(jsonRes({ ok: true }));
};
activeId = "conv-test";
messages = [];
$("input").value = "第一轮失败";
await send();
chatFail = false;
$("input").value = "第二轮成功";
await send();
assert(calls.filter(c => c.url.endsWith("/title")).length === 1, "titlePending retries summary after failed first round");

// 16. 会话侧栏：按钮是纯开关——点一下收起（无遮罩、不变暗），再点一下展开
const sideBar = $("sidebar");
sideBar.className = "";
$("btnSide")._handlers.click();
assert(sideBar.classList.contains("collapsed"), "btnSide collapses sidebar");
$("btnSide")._handlers.click();
assert(!sideBar.classList.contains("collapsed"), "btnSide expands sidebar again");

// 17. trimMessages：预算裁剪、工具轮完整性（配对回退/游离丢弃）、至少保留最近一轮
const long1 = "字".repeat(800);   // estTokens ≈ 960/条
let tList = [];
for (let i = 0; i < 10; i++) tList.push({ role: i % 2 ? "assistant" : "user", content: long1 });
const t1 = trimMessages(tList, 3000);   // 960×10 总 9600，预算 3000 → 裁到剩 3 条（2880 ≤ 3000）
assert(t1.length === 3 && t1[0].content === long1, "trim drops oldest until budget fits, keeps >=2: " + t1.length);
const tSingle = trimMessages([{ role: "user", content: "x" }], 1);
assert(tSingle.length === 1, "trim keeps single message");
const tFull = [{ role: "user", content: "hi" }, { role: "assistant", content: "yo" }];
assert(trimMessages(tFull, 10000) === tFull, "trim returns same array within budget");

// 工具轮完整性：裁剪点落在 tool 消息 → 回退整轮保留（assistant(tool_calls) + tool 必须成对）
const toolPair = [
  { role: "user", content: long1 },
  { role: "assistant", content: null, tool_calls: [{ id: "c1", type: "function", function: { name: "run_javascript", arguments: "{}" } }] },
  { role: "tool", tool_call_id: "c1", content: long1 },
  { role: "user", content: "好" },
];
const t3 = trimMessages(toolPair, 100);
assert(t3.length === 3 && t3[0].role === "assistant" && Array.isArray(t3[0].tool_calls) && t3[1].role === "tool",
  "trim rolls back to paired tool round: " + JSON.stringify(t3.map(m => m.role)));
// 游离 tool（无配对 assistant）→ 丢弃
const orphan = [
  { role: "user", content: long1 },
  { role: "tool", tool_call_id: "orphan", content: long1 },
  { role: "user", content: "好" },
];
const t4 = trimMessages(orphan, 100);
assert(t4.length === 1 && t4[0].role === "user", "trim drops orphan tool message: " + JSON.stringify(t4.map(m => m.role)));

// 18. estTokens / msgTokens：CJK 与 ASCII 粗略换算、图片固定计费
assert(estTokens("你好") === 3, "estTokens cjk ~1.2/char");
assert(estTokens("abc") === 1, "estTokens ascii ~0.3/char");
const imgTok = msgTokens({ role: "user", content: [{ type: "image_url", image_url: { url: "data:image/png;base64,AAAA" } }] });
assert(imgTok === 1004, "image part flat 1000 + per-message overhead: " + imgTok);

// 19. currentContextBudget：模型 context_length 的 60% 取整、缺省 32k、下限 2048
modelCtx = { "m-big": 100000, "m-tiny": 3000 };
modelSelect.value = "m-big";
assert(currentContextBudget() === 60000, "budget = 60% of context_length: " + currentContextBudget());
modelSelect.value = "m-tiny";
assert(currentContextBudget() === 2048, "budget floors at 2048: " + currentContextBudget());
modelSelect.value = "";
assert(currentContextBudget() === 19200, "default context 32k when unknown: " + currentContextBudget());
}
`;

await new Function("document", "localStorage", "assert", "makeEl", "fetch", "fetchMock", "fixtures", "sseBody", "jsonRes",
  vendorBundle + "\n" + script + tests + "\nreturn runTests();")(doc, ls, assert, makeEl, fetchFn, fetchMock, fixtures, sseBody, jsonRes);
process.exit(failed);