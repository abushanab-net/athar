// T120: Mermaid diagrams for ```mermaid code blocks, in the editor and on share pages. The library is loaded on first use
// only (a separate chunk), so pages without a diagram never download it.
type Mermaid = typeof import("mermaid").default;

let loading: Promise<Mermaid> | undefined;
let seq = 0;

/** Dark when the page's background is dark (the app's theme comes from MudBlazor's variables, not a class). */
function dark(): boolean {
  const m = getComputedStyle(document.body).backgroundColor.match(/\d+(\.\d+)?/g);
  if (!m) return false;
  const [r, g, b] = m.map(Number);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128;
}

function load(): Promise<Mermaid> {
  return (loading ??= import("mermaid").then(({ default: mermaid }) => {
    mermaid.initialize({
      startOnLoad: false,
      // Diagrams come from page authors and are shown on public share pages: sanitized output (DOMPurify), no HTML
      // labels, no click handlers.
      securityLevel: "strict",
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      theme: dark() ? "dark" : "default",
      fontFamily: "inherit",
    });
    return mermaid;
  }));
}

/** The diagram as SVG markup. Throws Mermaid's parse error (first line is readable) for invalid source. */
export async function renderMermaid(source: string): Promise<string> {
  const mermaid = await load();
  await mermaid.parse(source); // fails fast on bad syntax, before Mermaid draws anything into the page
  const { svg } = await mermaid.render(`athar-mermaid-${++seq}`, source);
  return svg;
}
