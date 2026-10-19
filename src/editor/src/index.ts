import { Editor, Extension, Mark, Node, mergeAttributes, type Content } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import Collaboration from "@tiptap/extension-collaboration";
import CollaborationCaret from "@tiptap/extension-collaboration-caret";
import { TableKit } from "@tiptap/extension-table";
import { TaskItem, TaskList } from "@tiptap/extension-list";
import Image from "@tiptap/extension-image";
import Mention from "@tiptap/extension-mention";
import Suggestion from "@tiptap/suggestion";
import CodeBlock from "@tiptap/extension-code-block";
import { HocuspocusProvider } from "@hocuspocus/provider";
import { Fragment, Slice, type MarkType } from "@tiptap/pm/model";
import { NodeSelection, Plugin, PluginKey, TextSelection } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { absolutePositionToRelativePosition, ySyncPluginKey } from "@tiptap/y-tiptap";
import * as Y from "yjs";
import { renderMermaid } from "./mermaid";

export type SaveState = "unsynced" | "synced" | "saved";
export type PageRef = { id: string; title: string };
export type Presence = { name: string; color: string; self: boolean };
/** One `/` menu entry. Labels and the icon (Material SVG path markup) come from the page, so they follow its language. */
export type SlashItem = { id: string; title: string; description: string; icon: string; group: string; keywords?: string };

export interface MountOptions {
  /** Collab WebSocket URL, e.g. ws://localhost:1234 */
  url: string;
  /** Page id, which is the Yjs document name. */
  page: string;
  /** Returns a fresh collab ticket. Called on every (re)connect, because tickets are single use. */
  token: () => Promise<string>;
  user: { name: string; color: string };
  /** FR-RT-05: "synced" means collab has every local update, "saved" means a persisted revision has them. */
  onSaveState?: (state: SaveState, revision?: number) => void;
  /** FR-RT-02: everyone on the page, including this tab. */
  onPresence?: (people: Presence[]) => void;
  /** Pages for the `[[` page-link picker. */
  searchPages?: (query: string) => Promise<PageRef[]>;
  /** People for the `@` mention picker: only those who can see the page (FR-CM-04). */
  searchPeople?: (query: string) => Promise<PageRef[]>;
  /** Where a page link points. */
  pageHref?: (id: string) => string;
  /** The editor's accessible name. */
  label?: string;
  /** The `/` command menu (Docmost-style); empty or absent turns it off. */
  slashItems?: SlashItem[];
  /** A `/` command the page carries out itself (e.g. `image` opens its URL dialog). */
  onTool?: (command: string) => void;
}

/** `:::callout{type=info}` in Markdown (docs/06 §6.7). */
const Callout = Node.create({
  name: "callout",
  group: "block",
  content: "block+",
  defining: true,
  addAttributes: () => ({ type: { default: "info" } }),
  parseHTML: () => [{ tag: "div[data-callout]", getAttrs: (el) => ({ type: (el as HTMLElement).dataset.callout }) }],
  renderHTML: ({ node, HTMLAttributes }) => ["div", mergeAttributes(HTMLAttributes, { "data-callout": node.attrs.type, class: "callout" }), 0],
});

/**
 * An inline comment's anchor (FR-CM-01). Collab adds it through the anchorComment operation, never the browser; it isn't
 * inclusive, so typing at its edges doesn't stretch it. Not in Markdown (docs/06 §6.7).
 */
const CommentAnchor = Mark.create({
  name: "commentAnchor",
  inclusive: false,
  excludes: "",
  addAttributes: () => ({ threadId: { default: null } }),
  parseHTML: () => [{ tag: "span[data-thread-id]", getAttrs: (el) => ({ threadId: (el as HTMLElement).dataset.threadId }) }],
  renderHTML: ({ mark }) => ["span", { class: "comment-anchor", "data-thread-id": mark.attrs.threadId }, 0],
  // FR-CM-02: copy and paste strip anchors, so a thread is never cloned.
  addProseMirrorPlugins() {
    const type = this.type;
    return [new Plugin({ props: { transformPasted: (slice) => new Slice(stripMark(slice.content, type), slice.openStart, slice.openEnd) } })];
  },
});

function stripMark(fragment: Fragment, type: MarkType): Fragment {
  const nodes: import("@tiptap/pm/model").Node[] = [];
  fragment.forEach((node) => nodes.push(node.isText ? node.mark(type.removeFromSet(node.marks)) : node.copy(stripMark(node.content, type))));
  return Fragment.from(nodes);
}

/**
 * Keeps the selection painted while the editor isn't focused, so it stays visible while a comment is written about it.
 * The browser stops drawing a selection once focus leaves; ProseMirror's own selection is untouched.
 */
const pendingKey = new PluginKey<DecorationSet>("pendingComment");
const PendingComment = Extension.create({
  name: "pendingCommentHighlight",
  addProseMirrorPlugins() {
    return [
      new Plugin<DecorationSet>({
        key: pendingKey,
        state: {
          init: () => DecorationSet.empty,
          apply(tr, set) {
            const meta = tr.getMeta(pendingKey) as { from: number; to: number } | null | undefined;
            if (meta === null) return DecorationSet.empty;
            if (meta) return DecorationSet.create(tr.doc, [Decoration.inline(meta.from, meta.to, { class: "comment-pending" })]);
            return set.map(tr.mapping, tr.doc);
          },
        },
        props: {
          decorations: (state) => pendingKey.getState(state),
          handleDOMEvents: {
            blur: (view) => {
              const { from, to } = view.state.selection;
              if (from !== to) view.dispatch(view.state.tr.setMeta(pendingKey, { from, to }));
              return false;
            },
            focus: (view) => {
              view.dispatch(view.state.tr.setMeta(pendingKey, null));
              return false;
            },
          },
        },
      }),
    ];
  },
});

/**
 * T120: a ```mermaid code block shows its diagram; the source is edited in place. Clicking the diagram puts the caret in
 * the source, and the source shows while the caret is in the block (`is-editing`) or when the diagram can't be drawn.
 * Same node type and attrs as the plain code block, so Yjs, Markdown (```mermaid) and agents are unchanged. Styles:
 * `.code-block` in app.css.
 */
const DiagramCodeBlock = CodeBlock.extend({
  addNodeView() {
    return ({ node, editor, getPos }) => {
      const dom = document.createElement("div");
      dom.className = "code-block";
      const preview = document.createElement("div");
      preview.className = "mermaid-diagram";
      preview.contentEditable = "false";
      const pre = document.createElement("pre");
      const code = document.createElement("code");
      pre.append(code);
      dom.append(preview, pre);
      let current = node;
      let timer = 0;
      let drawn: string | null = null;
      const draw = () => {
        const language = current.attrs.language as string | null;
        code.className = language ? `language-${language}` : "";
        dom.classList.toggle("is-mermaid", language === "mermaid");
        if (language !== "mermaid") {
          preview.replaceChildren();
          dom.classList.remove("mermaid-error");
          drawn = null;
          return;
        }
        const source = current.textContent;
        if (source === drawn) return;
        if (!source.trim()) {
          // A new, empty diagram block: nothing to draw yet, and not an error.
          window.clearTimeout(timer);
          preview.replaceChildren();
          dom.classList.remove("mermaid-error");
          drawn = source;
          return;
        }
        window.clearTimeout(timer);
        // Redraw shortly after typing stops; the first draw is immediate.
        timer = window.setTimeout(() => {
          drawn = source;
          renderMermaid(source)
            .then((svg) => {
              if (drawn !== source) return; // a newer edit is being drawn
              preview.innerHTML = svg; // sanitized by Mermaid (securityLevel "strict")
              dom.classList.remove("mermaid-error");
            })
            .catch((e: unknown) => {
              // A broken diagram never breaks the editor: show the source and the first line of the error.
              console.warn("Mermaid diagram not rendered; showing its source.", e);
              if (drawn !== source) return;
              preview.textContent = String((e as Error)?.message ?? e).split("\n")[0];
              dom.classList.add("mermaid-error");
            });
        }, drawn === null ? 0 : 400);
      };
      preview.addEventListener("mousedown", (e) => {
        const pos = getPos();
        if (!editor.isEditable || pos === undefined) return; // readers just see the diagram
        e.preventDefault();
        editor.chain().focus().setTextSelection(pos + current.nodeSize - 1).run();
      });
      draw();
      return {
        dom,
        contentDOM: code,
        update: (updated) => {
          if (updated.type !== current.type) return false;
          current = updated;
          draw();
          return true;
        },
        // The rendered diagram and our classes on the wrapper are ours, not document changes. Without this, ProseMirror
        // re-creates the view on every class toggle, which re-renders, which toggles again.
        ignoreMutation: (m) =>
          m.type !== "selection" && (preview.contains(m.target as globalThis.Node) || (m.type === "attributes" && m.target === dom)),
        stopEvent: (e) => preview.contains(e.target as globalThis.Node),
        destroy: () => window.clearTimeout(timer),
      };
    };
  },
});

/** Marks the code block that holds the caret (`is-editing`), so a diagram's source shows while it's being edited. */
const CodeBlockEditing = Extension.create({
  name: "codeBlockEditing",
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [
      new Plugin({
        props: {
          decorations: (state) => {
            if (!editor.isEditable) return null;
            const { $from } = state.selection;
            for (let d = $from.depth; d > 0; d--)
              if ($from.node(d).type.name === "codeBlock")
                return DecorationSet.create(state.doc, [Decoration.node($from.before(d), $from.after(d), { class: "is-editing" })]);
            return null;
          },
        },
      }),
    ];
  },
});

export type CommentSelection ={ from: string; to: string; quote: string } | { error: "empty" | "multi_block" };

/** Mounts a collaborative Tiptap editor into `el`. Call `destroy()` to unmount. */
export function mount(el: HTMLElement, opts: MountOptions) {
  let canWrite = false, readMode = false;
  const document = new Y.Doc();
  const provider = new HocuspocusProvider({
    url: opts.url,
    name: opts.page,
    document,
    token: opts.token,
    // Collab decides the level; the editor stays read-only until it says read-write (FR-RT-03).
    // Editable only with write access (collab decides) and not in Read mode (T115, U-2).
    onAuthenticated: ({ scope }) => { canWrite = scope === "read-write"; editor.setEditable(canWrite && !readMode); },
    onUnsyncedChanges: ({ number }) => opts.onSaveState?.(number ? "unsynced" : "synced"),
    onStateless: ({ payload }) => {
      const { saved } = JSON.parse(payload) as { saved?: number };
      if (saved !== undefined && !provider.hasUnsyncedChanges) opts.onSaveState?.("saved", saved);
    },
  });
  const pageHref = opts.pageHref ?? ((id: string) => `/wiki/p/${id}`);

  // `/` opens a command menu at the caret: type to filter (title, or English keywords in any UI language), arrows and
  // Enter to pick. The `/` and the query are replaced by the block. Not inside code, where `/` is just a character.
  type Range = { from: number; to: number };
  const at = (range: Range) => editor.chain().focus().deleteRange(range);
  const slashActions: Record<string, (range: Range) => void> = {
    text: (r) => at(r).setParagraph().run(),
    todo: (r) => at(r).toggleTaskList().run(),
    h1: (r) => at(r).setHeading({ level: 1 }).run(),
    h2: (r) => at(r).setHeading({ level: 2 }).run(),
    h3: (r) => at(r).setHeading({ level: 3 }).run(),
    bullet: (r) => at(r).toggleBulletList().run(),
    numbered: (r) => at(r).toggleOrderedList().run(),
    quote: (r) => at(r).toggleBlockquote().run(),
    code: (r) => at(r).setCodeBlock().run(),
    callout: (r) => at(r).wrapIn("callout").run(),
    divider: (r) => at(r).setHorizontalRule().run(),
    table: (r) => at(r).insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(),
    pageLink: (r) => at(r).insertContent("[[").run(),
    image: (r) => (at(r).run(), opts.onTool?.("image")),
  };
  const slashItems = (opts.slashItems ?? []).filter((i) => slashActions[i.id]);
  const SlashCommand = Extension.create({
    name: "slashCommand",
    addProseMirrorPlugins() {
      return [
        Suggestion<SlashItem>({
          editor: this.editor,
          pluginKey: new PluginKey("slashCommand"),
          char: "/",
          allow: ({ state }) => slashItems.length > 0 && !state.selection.$from.parent.type.spec.code,
          items: ({ query }) => {
            const q = query.toLowerCase();
            return slashItems.filter((i) => `${i.title} ${i.keywords ?? ""} ${i.id}`.toLowerCase().includes(q)).slice(0, 20);
          },
          command: ({ range, props }) => slashActions[props.id](range),
          render: slashMenu,
        }),
      ];
    },
  });

  const editor = new Editor({
    element: el,
    editable: false,
    textDirection: "auto", // dir="auto" on every block (FR-PG-02)
    editorProps: { attributes: { "aria-label": opts.label ?? "Page content", "aria-multiline": "true" } },
    extensions: [
      StarterKit.configure({ undoRedo: false, link: { openOnClick: false }, codeBlock: false }), // Yjs owns undo history
      DiagramCodeBlock,
      CodeBlockEditing,
      TableKit.configure({ table: { resizable: false, renderWrapper: true } }),
      TaskList,
      TaskItem.configure({ nested: true }),
      Image,
      Callout,
      CommentAnchor,
      PendingComment,
      // `[[` opens a page picker; the link is a node, so renaming the target doesn't break it (docs/06 §6.7 page links).
      Mention.extend({ name: "pageMention" }).configure({
        HTMLAttributes: { class: "page-mention" },
        renderHTML: ({ node }) => ["a", { href: pageHref(node.attrs.id), class: "page-mention", "data-page-id": node.attrs.id }, node.attrs.label ?? node.attrs.id],
        renderText: ({ node }) => `[[${node.attrs.label}]]`,
        suggestion: { char: "[[", items: async ({ query }) => (await opts.searchPages?.(query)) ?? [], render: pagePicker },
      }),
      // `@[Name](user:id)` mentions (docs/06 §6.7). Collab's persist notifies people newly mentioned who can see the page.
      Mention.configure({
        HTMLAttributes: { class: "user-mention" },
        renderText: ({ node }) => `@${node.attrs.label}`,
        suggestion: { char: "@", items: async ({ query }) => (await opts.searchPeople?.(query)) ?? [], render: pagePicker },
      }),
      SlashCommand,
      Collaboration.configure({ document }),
      CollaborationCaret.configure({ provider, user: opts.user }),
    ],
  });

  if (opts.onPresence) {
    // Awareness changes on every cursor move; report only when the set of people changes.
    let last = "";
    provider.awareness!.on("change", () => {
      const people = [...provider.awareness!.getStates()]
        .filter(([, s]) => s.user)
        .map(([id, s]) => ({ name: s.user.name as string, color: s.user.color as string, self: id === provider.awareness!.clientID }));
      const key = JSON.stringify(people);
      if (key !== last) opts.onPresence!(people);
      last = key;
    });
  }

  /** Inserts at the cursor; with a node selected (a just-inserted image), after it rather than over it. */
  const insert = (content: Content) => {
    const s = editor.state.selection;
    const chain = editor.chain().focus();
    return (s instanceof NodeSelection ? chain.insertContentAt(s.to, content) : chain.insertContent(content)).run();
  };

  /** Toolbar commands, called from the Blazor toolbar. */
  const commands: Record<string, (arg?: string) => void> = {
    undo: () => editor.chain().focus().undo().run(),
    redo: () => editor.chain().focus().redo().run(),
    bold: () => editor.chain().focus().toggleBold().run(),
    italic: () => editor.chain().focus().toggleItalic().run(),
    underline: () => editor.chain().focus().toggleUnderline().run(),
    strike: () => editor.chain().focus().toggleStrike().run(),
    code: () => editor.chain().focus().toggleCode().run(),
    clearFormatting: () => editor.chain().focus().unsetAllMarks().clearNodes().run(),
    heading: (level) => editor.chain().focus().toggleHeading({ level: Number(level ?? 2) as 1 | 2 | 3 }).run(),
    paragraph: () => editor.chain().focus().setParagraph().run(),
    blockquote: () => editor.chain().focus().toggleBlockquote().run(),
    horizontalRule: () => editor.chain().focus().setHorizontalRule().run(),
    bulletList: () => editor.chain().focus().toggleBulletList().run(),
    orderedList: () => editor.chain().focus().toggleOrderedList().run(),
    taskList: () => editor.chain().focus().toggleTaskList().run(),
    codeBlock: () => editor.chain().focus().toggleCodeBlock().run(),
    table: () => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(),
    // Row and column actions act on the table the caret is in; outside a table they do nothing.
    addRow: () => editor.chain().focus().addRowAfter().run(),
    addColumn: () => editor.chain().focus().addColumnAfter().run(),
    deleteRow: () => editor.chain().focus().deleteRow().run(),
    deleteColumn: () => editor.chain().focus().deleteColumn().run(),
    deleteTable: () => editor.chain().focus().deleteTable().run(),
    callout: () => editor.chain().focus().wrapIn("callout").run(),
    image: (src) => src && insert({ type: "image", attrs: { src } }),
    link: (href) => (href ? editor.chain().focus().extendMarkRange("link").setLink({ href }).run() : editor.chain().focus().unsetLink().run()),
    pageLink: () => editor.chain().focus().insertContent("[[").run(),
    // An uploaded file that isn't an image: its name, linked to /attachments/{id}. `arg` is JSON {href, name}.
    file: (arg) => {
      const { href, name } = JSON.parse(arg ?? "{}") as { href?: string; name?: string };
      if (href) insert([{ type: "text", text: name || href, marks: [{ type: "link", attrs: { href } }] }, { type: "text", text: " " }]);
    },
  };

  /** The current selection as base64 Yjs relative positions plus its text, for an inline comment (FR-CM-01). */
  function commentSelection(): CommentSelection {
    const { from, to, $from, $to } = editor.state.selection;
    if (from === to) return { error: "empty" };
    // ponytail: collab anchors within one text block; a selection across blocks is refused.
    if ($from.parent !== $to.parent) return { error: "multi_block" };
    const sync = ySyncPluginKey.getState(editor.state) as { type: Y.XmlFragment; binding: { mapping: Map<Y.AbstractType<unknown>, unknown> } };
    const rel = (pos: number) =>
      btoa(String.fromCharCode(...Y.encodeRelativePosition(absolutePositionToRelativePosition(pos, sync.type, sync.binding.mapping as never))));
    return { from: rel(from), to: rel(to), quote: editor.state.doc.textBetween(from, to) };
  }

  /** Scrolls a thread's anchor into view and flashes it. */
  function focusThread(threadId: string) {
    const el = editor.view.dom.querySelector(`span.comment-anchor[data-thread-id="${CSS.escape(threadId)}"]`);
    if (!el) return false;
    el.scrollIntoView({ block: "center", behavior: "smooth" });
    el.classList.add("comment-anchor--focus");
    setTimeout(() => el.classList.remove("comment-anchor--focus"), 1500);
    return true;
  }

  /** After a comment is made from the selection: drop the kept highlight and collapse the selection to its end. */
  function clearCommentSelection() {
    const { to } = editor.state.selection;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, to)).setMeta(pendingKey, null));
  }

  // FR-CM-05: resolved threads are hidden by default, and so are their highlights. A style rule per thread survives
  // re-renders (ProseMirror redraws the anchor spans); the mark itself stays, so reopening brings the highlight back.
  const resolvedStyle = window.document.createElement("style");
  el.append(resolvedStyle);
  function setResolvedThreads(ids: string[]) {
    resolvedStyle.textContent = ids
      // Outranks the page's own anchor style (.page-editor[b-…] .comment-anchor).
      .map((id) => `.page-editor .ProseMirror span.comment-anchor[data-thread-id="${CSS.escape(id)}"]{background:none;border-block-end:0}`)
      .join("\n");
  }

  /** Read mode (T115): the page read-only for an editor too; write access still comes only from collab. */
  function setReadMode(on: boolean) {
    readMode = on;
    editor.setEditable(canWrite && !readMode);
  }

  /** The headings for the outline (T115): level, text and position. */
  function outline() {
    const out: { level: number; text: string; pos: number }[] = [];
    editor.state.doc.descendants((node, pos) => {
      if (node.type.name !== "heading") return true;
      out.push({ level: node.attrs.level, text: node.textContent, pos });
      return false;
    });
    return out;
  }

  function scrollToHeading(pos: number) {
    (editor.view.nodeDOM(pos) as HTMLElement | null)?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  /** Words in the page, blocks separated so words across them don't merge. */
  function wordCount() {
    const text = editor.state.doc.textBetween(0, editor.state.doc.content.size, " ", " ").trim();
    return text ? text.split(/\s+/).length : 0;
  }

  return {
    editor,
    provider,
    setReadMode,
    outline,
    scrollToHeading,
    wordCount,
    commentSelection,
    clearCommentSelection,
    setResolvedThreads,
    focusThread,
    run: (command: string, arg?: string) => commands[command]?.(arg),
    destroy() {
      editor.destroy();
      provider.destroy();
      document.destroy();
    },
  };
}

/** The `/` command menu: grouped entries with an icon, a title and a line of help (styles: .slash-menu in app.css). */
function slashMenu() {
  let box: HTMLDivElement;
  let items: SlashItem[] = [];
  let selected = 0;
  let pick: (item: SlashItem) => void;
  const option = (item: SlashItem, i: number) => {
    const b = document.createElement("button");
    b.type = "button";
    b.role = "option";
    b.id = `slash-${item.id}`;
    b.className = "slash-item";
    b.setAttribute("aria-selected", String(i === selected));
    const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    icon.setAttribute("viewBox", "0 0 24 24");
    icon.setAttribute("aria-hidden", "true");
    icon.innerHTML = item.icon; // Material icon paths from the page, never user input
    const text = document.createElement("span");
    const title = document.createElement("strong");
    title.textContent = item.title;
    title.dir = "auto";
    const help = document.createElement("small");
    help.textContent = item.description;
    help.dir = "auto";
    text.append(title, help);
    const badge = document.createElement("span");
    badge.className = "slash-icon";
    badge.append(icon);
    b.append(badge, text);
    b.onmousedown = (e) => (e.preventDefault(), pick(item));
    return b;
  };
  const draw = () => {
    const nodes: HTMLElement[] = [];
    let group = "";
    items.forEach((item, i) => {
      if (item.group !== group) {
        group = item.group;
        const h = document.createElement("div");
        h.className = "slash-group";
        h.textContent = group;
        h.setAttribute("role", "presentation");
        nodes.push(h);
      }
      nodes.push(option(item, i));
    });
    if (!items.length) {
      const none = document.createElement("div");
      none.className = "slash-empty";
      none.textContent = "—";
      nodes.push(none);
    }
    box.replaceChildren(...nodes);
    const current = box.querySelector<HTMLElement>('[aria-selected="true"]');
    current?.scrollIntoView({ block: "nearest" });
    if (current) box.setAttribute("aria-activedescendant", current.id);
  };
  // Below the caret, or above it when there isn't room (as Docmost does near the bottom of the window).
  const place = (rect: DOMRect | null | undefined) => {
    if (!rect) return;
    const height = Math.min(box.scrollHeight, 360);
    const below = window.innerHeight - rect.bottom > height + 8;
    box.style.top = `${(below ? rect.bottom + 4 : rect.top - height - 4) + window.scrollY}px`;
    box.style.left = `${Math.min(rect.left, window.innerWidth - box.offsetWidth - 8) + window.scrollX}px`;
  };
  type Props = { items: SlashItem[]; command: (item: SlashItem) => void; clientRect?: (() => DOMRect | null) | null };
  const update = (p: Props) => {
    ({ items, command: pick } = p);
    selected = 0;
    draw();
    place(p.clientRect?.());
  };
  return {
    onStart: (p: Props) => {
      box = document.createElement("div");
      box.className = "slash-menu";
      box.role = "listbox";
      document.body.append(box);
      update(p);
    },
    onUpdate: update,
    onKeyDown: ({ event }: { event: KeyboardEvent }) => {
      if (!items.length) return false;
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        selected = (selected + (event.key === "ArrowDown" ? 1 : items.length - 1)) % items.length;
        draw();
        return true;
      }
      if (event.key === "Enter" || event.key === "Tab") {
        pick(items[selected]);
        return true;
      }
      return false;
    },
    onExit: () => box?.remove(),
  };
}

/** A small keyboard-driven list under the caret for the `[[` page picker. */
function pagePicker() {
  let box: HTMLDivElement;
  let items: PageRef[] = [];
  let selected = 0;
  let pick: (item: { id: string; label: string }) => void;
  const draw = () => {
    box.replaceChildren(
      ...items.map((item, i) => {
        const b = document.createElement("button");
        b.type = "button";
        b.role = "option";
        b.textContent = item.title;
        b.dir = "auto";
        b.setAttribute("aria-selected", String(i === selected));
        b.style.cssText = `display:block;width:100%;text-align:start;padding:4px 8px;border:0;cursor:pointer;font:inherit;background:${i === selected ? "var(--mud-palette-background-gray, #eef1f5)" : "transparent"}`;
        b.onmousedown = (e) => (e.preventDefault(), pick({ id: item.id, label: item.title }));
        return b;
      }),
    );
  };
  const place = (rect: DOMRect | null | undefined) => {
    if (!rect) return;
    box.style.top = `${rect.bottom + window.scrollY + 4}px`;
    box.style.left = `${rect.left + window.scrollX}px`;
  };
  return {
    onStart: (p: { items: PageRef[]; command: typeof pick; clientRect?: (() => DOMRect | null) | null }) => {
      box = document.createElement("div");
      box.className = "page-picker";
      box.role = "listbox";
      box.style.cssText = "position:absolute;z-index:2000;min-width:14rem;max-width:24rem;background:var(--mud-palette-surface, #fff);color:var(--mud-palette-text-primary, #14213d);border:1px solid var(--mud-palette-lines-default, #dfe3ea);border-radius:6px;box-shadow:0 8px 24px rgb(20 33 61 / 0.14);padding:4px 0";
      document.body.append(box);
      ({ items, command: pick } = p);
      selected = 0;
      place(p.clientRect?.());
      draw();
    },
    onUpdate: (p: { items: PageRef[]; command: typeof pick; clientRect?: (() => DOMRect | null) | null }) => {
      ({ items, command: pick } = p);
      selected = 0;
      place(p.clientRect?.());
      draw();
    },
    onKeyDown: ({ event }: { event: KeyboardEvent }) => {
      if (!items.length) return false;
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        selected = (selected + (event.key === "ArrowDown" ? 1 : items.length - 1)) % items.length;
        draw();
        return true;
      }
      if (event.key === "Enter") {
        pick({ id: items[selected].id, label: items[selected].title });
        return true;
      }
      return false;
    },
    onExit: () => box?.remove(),
  };
}
