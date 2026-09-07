// 第三方前端依赖打包：markdown-it（Markdown 渲染）+ highlight.js 常用语言子集（代码高亮）
// + KaTeX（LaTeX 数学公式，auto-render 在 DOM 上渲染 $...$/$$...$$）。
// 产物 vendor.bundle.js（IIFE 全局 myvendor = { MarkdownIt, hljs, katex, renderMathInElement }）
// 与 katex.css（字体 base64 内嵌）嵌入 exe，网页端零 CDN。
// 用法: cd tools/vendor && npm install && npm run build
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { build } from "esbuild";

// highlight.js 语言子集：覆盖 Web 聊天中常见代码语言（core + 各语言模块）
const languages = {
  javascript: "javascript",
  typescript: "typescript",
  python: "python",
  bash: "bash",
  json: "json",
  xml: "xml",
  css: "css",
  csharp: "csharp",
  java: "java",
  go: "go",
  rust: "rust",
  sql: "sql",
  yaml: "yaml",
  ini: "ini",
  diff: "diff",
  markdown: "markdown",
};

const entry = `
import MarkdownIt from "markdown-it";
import hljs from "highlight.js/lib/core";
import katex from "katex";
import renderMathInElement from "katex/contrib/auto-render";
${Object.entries(languages)
  .map(([name, file]) => `import ${name.replace(/[^a-zA-Z]/g, "_")} from "highlight.js/lib/languages/${file}";`)
  .join("\n")}
${Object.entries(languages)
  .map(([name, file]) => `hljs.registerLanguage(${JSON.stringify(file)}, ${name.replace(/[^a-zA-Z]/g, "_")});`)
  .join("\n")}
export { MarkdownIt, hljs, katex, renderMathInElement };
`;

await build({
  stdin: {
    contents: entry,
    resolveDir: process.cwd(),
    sourcefile: "vendor-entry.js",
    loader: "js",
  },
  bundle: true,
  minify: true,
  format: "iife",
  globalName: "myvendor",
  outfile: "vendor.bundle.js",
  target: ["es2018"],
});
console.log("vendor.bundle.js built");

// ── katex.css：字体全部 base64 内嵌（现代浏览器只用 woff2，woff/ttf 引用删除）──
const katexCss = readFileSync(join("node_modules", "katex", "dist", "katex.min.css"), "utf8");
const fontsDir = join("node_modules", "katex", "dist", "fonts");
const cssInline = katexCss
  .replace(/url\(fonts\/([^)]+\.woff2)\)/g, (_, f) =>
    `url(data:font/woff2;base64,${readFileSync(join(fontsDir, f)).toString("base64")})`)
  .replace(/,\s*url\(fonts\/[^)]+\.(?:woff|ttf)\)\s*format\("(?:woff|truetype)"\)/g, "");
writeFileSync("katex.css", cssInline);
console.log("katex.css built (" + (cssInline.length / 1024).toFixed(0) + "KB)");
