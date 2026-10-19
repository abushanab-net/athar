import MarkdownIt from "markdown-it";
import type StateBlock from "markdown-it/lib/rules_block/state_block.mjs";
import type Token from "markdown-it/lib/token.mjs";
import * as Y from "yjs";

// Markdown projection (FR-PG-04, docs/06 §6.7): ProseMirror JSON ⇄ Markdown with the Athar directives, and
// ProseMirror JSON → Yjs for server-side operations. Node and mark names are the editor's (src/editor).

export type Mark = { type: string; attrs?: Record<string, unknown> };
export type PMNode = { type: string; attrs?: Record<string, unknown>; content?: PMNode[]; text?: string; marks?: Mark[] };

// ---------- ProseMirror JSON → Markdown ----------

export function toMarkdown(doc: PMNode): string {
  return blocks(doc.content ?? []);
}

const blocks = (nodes: PMNode[]) => nodes.map(block).join("\n\n");

/** `{dir=rtl}` only when a block sets a direction other than the editor-wide `auto`. */
const dir = (n: PMNode) => (n.attrs?.dir === "rtl" || n.attrs?.dir === "ltr" ? ` {dir=${n.attrs.dir}}` : "");

function block(n: PMNode): string {
  const c = n.content ?? [];
  switch (n.type) {
    case "paragraph":
      return escapeLineStart(inline(c)) + dir(n);
    case "heading":
      return `${"#".repeat(Number(n.attrs?.level ?? 1))} ${inline(c)}${dir(n)}`;
    case "bulletList":
      return c.map((li) => item("- ", li)).join("\n");
    case "orderedList": {
      const start = Number(n.attrs?.start ?? 1);
      return c.map((li, i) => item(`${start + i}. `, li)).join("\n");
    }
    case "taskList":
      return c.map((li) => item(li.attrs?.checked ? "- [x] " : "- [ ] ", li)).join("\n");
    case "blockquote":
      return blocks(c).split("\n").map((l) => (l ? `> ${l}` : ">")).join("\n");
    case "codeBlock": {
      const text = c.map((t) => t.text ?? "").join("");
      const fence = text.includes("```") ? "~~~~" : "```";
      return `${fence}${n.attrs?.language ?? ""}\n${text}\n${fence}`;
    }
    case "horizontalRule":
      return "---";
    case "image":
      return image(n);
    case "table":
      return table(n);
    case "callout":
      return `:::callout{type=${n.attrs?.type ?? "info"}}\n${blocks(c)}\n:::`;
    default:
      return blocks(c); // unknown wrapper: keep its content
  }
}

/** A list item: the marker on the first line, continuation lines indented to the marker's width. */
function item(marker: string, li: PMNode) {
  const pad = " ".repeat(marker.length);
  // A nested list follows its paragraph directly, so the list stays tight.
  const body = (li.content ?? []).map((b, i) => (i === 0 ? "" : /List$/.test(b.type) ? "\n" : "\n\n") + block(b)).join("");
  return marker + body.split("\n").map((l, i) => (i === 0 || !l ? l : pad + l)).join("\n");
}

const image = (n: PMNode) =>
  `![${escapeText(String(n.attrs?.alt ?? ""))}](${n.attrs?.src ?? ""}${n.attrs?.title ? ` "${String(n.attrs.title).replace(/"/g, '\\"')}"` : ""})`;

function table(n: PMNode): string {
  const rows = n.content ?? [];
  const merged = rows.some((r) => (r.content ?? []).some((c) => Number(c.attrs?.colspan ?? 1) > 1 || Number(c.attrs?.rowspan ?? 1) > 1));
  const multiBlock = rows.some((r) => (r.content ?? []).some((c) => (c.content ?? []).length > 1));
  if (merged || multiBlock) return htmlTable(rows); // docs/06 §6.7: merged cells come out as HTML
  const cell = (c: PMNode) => inline(c.content?.[0]?.content ?? []); // inline() already escapes `|`
  const line = (r: PMNode) => `| ${(r.content ?? []).map(cell).join(" | ")} |`;
  const [head, ...body] = rows;
  return [line(head), `| ${(head.content ?? []).map(() => "---").join(" | ")} |`, ...body.map(line)].join("\n");
}

function htmlTable(rows: PMNode[]) {
  const cell = (c: PMNode) => {
    const tag = c.type === "tableHeader" ? "th" : "td";
    const span = ["colspan", "rowspan"].filter((a) => Number(c.attrs?.[a] ?? 1) > 1).map((a) => ` ${a}="${c.attrs![a]}"`).join("");
    return `<${tag}${span}>${(c.content ?? []).map((p) => html(p.content ?? [])).join("<br>")}</${tag}>`;
  };
  return `<table>\n${rows.map((r) => `<tr>${(r.content ?? []).map(cell).join("")}</tr>`).join("\n")}\n</table>`;
}

const html = (nodes: PMNode[]) =>
  nodes.map((t) => (t.text ?? (t.attrs?.label as string) ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")).join("");

// Marks open outermost first, so `**[a](x)**` and `[**a**](x)` serialize the same way every time.
const MARK_ORDER = ["link", "bold", "italic", "strike", "underline", "code"];
const OPEN: Record<string, (m: Mark) => string> = {
  link: () => "[",
  bold: () => "**",
  italic: () => "*",
  strike: () => "~~",
  underline: () => "<u>",
  code: () => "`",
};
const CLOSE: Record<string, (m: Mark) => string> = {
  link: (m) => `](${m.attrs?.href ?? ""})`,
  bold: () => "**",
  italic: () => "*",
  strike: () => "~~",
  underline: () => "</u>",
  code: () => "`",
};

const sameMark = (a: Mark, b: Mark) => a.type === b.type && (a.type !== "link" || a.attrs?.href === b.attrs?.href);

function inline(nodes: PMNode[]): string {
  let out = "";
  const open: Mark[] = [];
  const closeTo = (keep: number) => {
    while (open.length > keep) out += CLOSE[open.at(-1)!.type](open.pop()!);
  };
  for (const n of nodes) {
    const marks = (n.type === "text" ? (n.marks ?? []) : []).filter((m) => OPEN[m.type]).sort((a, b) => MARK_ORDER.indexOf(a.type) - MARK_ORDER.indexOf(b.type));
    // Keep the longest prefix of open marks this node still has, close the rest, open what's new.
    let keep = 0;
    while (keep < open.length && keep < marks.length && sameMark(open[keep], marks[keep])) keep++;
    closeTo(keep);
    for (const m of marks.slice(keep)) {
      out += OPEN[m.type](m);
      open.push(m);
    }
    const code = marks.some((m) => m.type === "code");
    if (n.type === "text") out += code ? (n.text ?? "") : escapeText(n.text ?? "");
    else if (n.type === "hardBreak") out += "\\\n";
    else if (n.type === "pageMention") out += `[[${escapeText(String(n.attrs?.label ?? ""))}]](page:${n.attrs?.id})`;
    else if (n.type === "mention") out += `@[${escapeText(String(n.attrs?.label ?? ""))}](user:${n.attrs?.id})`;
    else if (n.type === "image") out += image(n);
  }
  closeTo(0);
  return out;
}

const escapeText = (s: string) => s.replace(/[\\`*_[\]<>~|]/g, "\\$&");
/** Text that would read as a block marker at the start of a line (heading, quote, list, fence, directive). */
const escapeLineStart = (s: string) => s.replace(/^(#{1,6}\s|>|[-+]\s|\d+[.)]\s|:::|```|~~~)/, (m) => "\\" + m);

// ---------- Markdown → ProseMirror JSON ----------

const md = new MarkdownIt({ html: true, linkify: false }).use(callouts);

export function fromMarkdown(markdown: string): PMNode {
  const tokens = md.parse(markdown, {});
  const doc: PMNode = { type: "doc", content: [] };
  const stack: PMNode[] = [doc];
  const push = (n: PMNode) => (stack.at(-1)!.content ??= []).push(n);
  const open = (n: PMNode) => {
    push(n);
    stack.push(n);
  };
  for (const t of tokens) {
    switch (t.type) {
      case "paragraph_open":
        open({ type: "paragraph", content: [] });
        break;
      case "heading_open":
        open({ type: "heading", attrs: { level: Number(t.tag.slice(1)) }, content: [] });
        break;
      case "bullet_list_open":
        open({ type: "bulletList", content: [] });
        break;
      case "ordered_list_open":
        open({ type: "orderedList", attrs: { start: Number(t.attrGet("start") ?? 1) }, content: [] });
        break;
      case "list_item_open":
        open({ type: "listItem", content: [] });
        break;
      case "blockquote_open":
        open({ type: "blockquote", content: [] });
        break;
      case "callout_open":
        open({ type: "callout", attrs: { type: t.attrGet("type") ?? "info" }, content: [] });
        break;
      case "table_open":
        open({ type: "table", content: [] });
        break;
      case "tr_open":
        open({ type: "tableRow", content: [] });
        break;
      case "th_open":
      case "td_open":
        open({ type: t.type === "th_open" ? "tableHeader" : "tableCell", attrs: { colspan: 1, rowspan: 1, colwidth: null }, content: [] });
        open({ type: "paragraph", content: [] });
        break;
      case "th_close":
      case "td_close":
        stack.pop();
        stack.pop();
        break;
      case "paragraph_close":
      case "heading_close":
      case "bullet_list_close":
      case "ordered_list_close":
      case "list_item_close":
      case "blockquote_close":
      case "callout_close":
      case "table_close":
      case "tr_close":
        finish(stack.pop()!);
        break;
      case "inline":
        (stack.at(-1)!.content ??= []).push(...inlineTokens(t.children ?? []));
        break;
      case "fence":
      case "code_block": {
        const text = t.content.replace(/\n$/, "");
        push({ type: "codeBlock", attrs: { language: t.info.trim() || null }, content: text ? [{ type: "text", text }] : [] });
        break;
      }
      case "hr":
        push({ type: "horizontalRule" });
        break;
      case "html_block":
        // ponytail: raw HTML (e.g. a merged-cell table) is kept as text, not rebuilt into nodes.
        push({ type: "paragraph", content: [{ type: "text", text: t.content.trim() }] });
        break;
    }
  }
  finish(doc);
  return doc;
}

/** Post-processing once a node's children are known. */
function finish(n: PMNode) {
  if (n.type === "paragraph" || n.type === "heading") {
    // A trailing `{dir=rtl}` sets the block's direction.
    const last = n.content?.at(-1);
    const m = last?.type === "text" && !last.marks?.length ? last.text!.match(/\s?\{dir=(rtl|ltr)\}$/) : null;
    if (m) {
      last!.text = last!.text!.slice(0, -m[0].length);
      if (!last!.text) n.content!.pop();
      n.attrs = { ...n.attrs, dir: m[1] };
    }
  }
  if (n.type === "bulletList") {
    // `- [ ]` / `- [x]` on every item makes a task list.
    const box = /^\[( |x|X)\] /;
    const items = n.content ?? [];
    const first = (li: PMNode) => li.content?.[0]?.content?.[0];
    if (items.length && items.every((li) => { const f = first(li); return f?.type === "text" && box.test(f.text!); })) {
      n.type = "taskList";
      for (const li of items) {
        const f = first(li)!;
        li.type = "taskItem";
        li.attrs = { checked: f.text![1].toLowerCase() === "x" };
        f.text = f.text!.slice(4);
        if (!f.text) li.content![0].content!.shift();
      }
    }
  }
  // Images are block nodes in the editor (an image inside a paragraph is invalid and the editor drops it): lift them out.
  if (n.type !== "paragraph" && n.type !== "heading" && n.content?.some((c) => c.type === "paragraph" && c.content?.some((x) => x.type === "image"))) {
    n.content = n.content.flatMap((c) => (c.type === "paragraph" && c.content?.some((x) => x.type === "image") ? liftImages(c) : [c]));
  }
}

function liftImages(p: PMNode): PMNode[] {
  const out: PMNode[] = [];
  let run: PMNode[] = [];
  const flush = () => {
    const trimmed = run.filter((x) => x.type !== "text" || x.text!.trim());
    if (trimmed.length) out.push({ ...p, content: run });
    run = [];
  };
  for (const c of p.content ?? []) {
    if (c.type === "image") {
      flush();
      out.push(c);
    } else run.push(c);
  }
  flush();
  return out;
}

function inlineTokens(tokens: Token[]): PMNode[] {
  const out: PMNode[] = [];
  const marks: Mark[] = [];
  const toggle = (type: string, on: boolean, attrs?: Record<string, unknown>) => {
    if (on) marks.push(attrs ? { type, attrs } : { type });
    else marks.splice(marks.map((m) => m.type).lastIndexOf(type), 1);
  };
  const text = (s: string, extra: Mark[] = []) => {
    if (!s) return;
    const all = [...marks, ...extra];
    const prev = out.at(-1);
    if (prev?.type === "text" && JSON.stringify(prev.marks ?? []) === JSON.stringify(all)) prev.text += s;
    else out.push(all.length ? { type: "text", text: s, marks: all } : { type: "text", text: s });
  };
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    switch (t.type) {
      case "text":
        text(t.content);
        break;
      case "code_inline":
        text(t.content, [{ type: "code" }]);
        break;
      case "strong_open":
      case "strong_close":
        toggle("bold", t.nesting === 1);
        break;
      case "em_open":
      case "em_close":
        toggle("italic", t.nesting === 1);
        break;
      case "s_open":
      case "s_close":
        toggle("strike", t.nesting === 1);
        break;
      case "html_inline":
        if (/^<u>$/i.test(t.content)) toggle("underline", true);
        else if (/^<\/u>$/i.test(t.content)) toggle("underline", false);
        else if (/^<br\s*\/?>$/i.test(t.content)) out.push({ type: "hardBreak" });
        else text(t.content);
        break;
      case "hardbreak":
        out.push({ type: "hardBreak" });
        break;
      case "softbreak":
        text(" ");
        break;
      case "image":
        out.push({ type: "image", attrs: { src: t.attrGet("src"), alt: t.content || null, title: t.attrGet("title") || null } });
        break;
      case "link_open": {
        const href = t.attrGet("href") ?? "";
        const directive = href.match(/^(page|user):(.+)$/);
        if (directive) {
          // `[[Title]](page:id)` and `@[Name](user:id)` (docs/06 §6.7): the link text is the label.
          let label = "";
          for (i++; i < tokens.length && tokens[i].type !== "link_close"; i++) label += tokens[i].content;
          if (directive[1] === "page") {
            out.push({ type: "pageMention", attrs: { id: directive[2], label: label.replace(/^\[(.*)\]$/s, "$1") } });
          } else {
            const prev = out.at(-1);
            if (prev?.type === "text" && prev.text!.endsWith("@")) {
              prev.text = prev.text!.slice(0, -1);
              if (!prev.text) out.pop();
            }
            out.push({ type: "mention", attrs: { id: directive[2], label } });
          }
        } else toggle("link", true, { href });
        break;
      }
      case "link_close":
        toggle("link", false);
        break;
    }
  }
  return out;
}

/** `:::callout{type=info}` … `:::`, nesting allowed. */
function callouts(markdown: MarkdownIt) {
  const line = (s: StateBlock, n: number) => s.src.slice(s.bMarks[n] + s.tShift[n], s.eMarks[n]).trim();
  markdown.block.ruler.before(
    "fence",
    "callout",
    (state, startLine, endLine, silent) => {
      const m = line(state, startLine).match(/^:::callout(?:\{type=([\w-]+)\})?$/);
      if (!m) return false;
      if (silent) return true;
      let end = startLine + 1;
      for (let depth = 1; end < endLine; end++) {
        const l = line(state, end);
        if (l.startsWith(":::callout")) depth++;
        else if (l === ":::" && --depth === 0) break;
      }
      const open = state.push("callout_open", "div", 1);
      open.attrSet("type", m[1] ?? "info");
      const lineMax = state.lineMax;
      state.lineMax = end;
      state.md.block.tokenize(state, startLine + 1, end);
      state.lineMax = lineMax;
      state.push("callout_close", "div", -1);
      state.line = end + 1;
      return true;
    },
    { alt: ["paragraph", "reference", "blockquote", "list"] },
  );
}

// ---------- ProseMirror JSON → Yjs (Tiptap's `default` fragment) ----------

/** Yjs elements for ProseMirror nodes, encoded the way y-prosemirror does: marks are text attributes keyed by mark name. */
export function toYjs(nodes: PMNode[]): (Y.XmlElement | Y.XmlText)[] {
  const out: (Y.XmlElement | Y.XmlText)[] = [];
  let run: Y.XmlText | null = null;
  let at = 0;
  for (const n of nodes) {
    if (n.type === "text") {
      if (!run) {
        run = new Y.XmlText();
        at = 0;
        out.push(run);
      }
      run.insert(at, n.text ?? "", Object.fromEntries((n.marks ?? []).map((m) => [m.type, m.attrs ?? {}])));
      at += (n.text ?? "").length;
      continue;
    }
    run = null;
    const el = new Y.XmlElement(n.type);
    for (const [k, v] of Object.entries(n.attrs ?? {})) if (v !== null && v !== undefined) el.setAttribute(k, v as string);
    if (n.content?.length) el.insert(0, toYjs(n.content));
    out.push(el);
  }
  return out;
}

/** ProseMirror JSON straight from the Yjs tree, so collab needs no editor schema. */
export function fromYjs(node: Y.XmlFragment | Y.XmlElement): PMNode[] {
  return node.toArray().flatMap((child): PMNode[] => {
    if (child instanceof Y.XmlText)
      return child.toDelta().map((d: { insert: string; attributes?: Record<string, Record<string, unknown> | null> }) => ({
        type: "text",
        text: d.insert,
        ...(d.attributes && { marks: Object.entries(d.attributes).map(([type, attrs]) => (attrs && Object.keys(attrs).length ? { type, attrs } : { type })) }),
      }));
    if (child instanceof Y.XmlElement) {
      const attrs = child.getAttributes();
      const content = fromYjs(child);
      return [{ type: child.nodeName, ...(Object.keys(attrs).length && { attrs }), ...(content.length && { content }) }];
    }
    return [];
  });
}
