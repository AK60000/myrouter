// 前端核心逻辑验证：用最小 DOM stub 执行 index.html 的真实 <script>，
// 验证思维链提取、renderContent 返回值、finishStream 光标/占位清理。
// 用法: node tools/_verify_frontend.mjs
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const html = readFileSync(join(root, "content", "index.html"), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
if (!script) { console.error("[FAIL] 未找到 <script> 块"); process.exit(1); }

// ── 最小 DOM stub ──
function makeEl(tag) {
  return {
    tagName: tag, className: "", innerHTML: "", textContent: "",
    children: [], style: {}, value: "", checked: false, disabled: false, files: [],
    appendChild(c) { this.children.push(c); c.parentNode = this; return c; },
    prepend(c) { this.children.unshift(c); c.parentNode = this; },
    append(...cs) { cs.forEach(c => this.appendChild(c)); },
    querySelector() { return makeEl("div"); },
    querySelectorAll() { return []; },
    addEventListener() {},
    remove() {},
    focus() {},
    scrollIntoView() {},
    classList: { add() {}, remove() {}, contains() { return false; } },
  };
}
const doc = { getElementById: () => makeEl("div"), createElement: t => makeEl(t) };
const ls = { getItem: () => null, setItem() {} };

let failed = 0;
const assert = (cond, msg) => {
  console.log((cond ? "[OK] " : "[FAIL] ") + msg);
  if (!cond) failed = 1;
};

// 测试代码追加进 script 作用域，直接访问模块级 let 变量
const tests = `
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
`;

new Function("document", "localStorage", "assert", "makeEl", script + tests)(doc, ls, assert, makeEl);
process.exit(failed);
