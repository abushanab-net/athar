// T120: share pages (server-rendered, read-only) load this one script only when they contain a ```mermaid block. Each
// block's source stays in the page: it's hidden once the diagram renders, and shown if rendering fails or scripts are off.
import { renderMermaid } from "./mermaid";

document.querySelectorAll<HTMLElement>("pre.mermaid-source > code.language-mermaid").forEach((code) => {
  const pre = code.parentElement!;
  renderMermaid(code.textContent ?? "")
    .then((svg) => {
      const diagram = document.createElement("div");
      diagram.className = "mermaid-diagram";
      diagram.innerHTML = svg; // sanitized by Mermaid (securityLevel "strict")
      pre.before(diagram);
      pre.hidden = true;
    })
    .catch((e) => console.warn("Mermaid diagram not rendered; showing its source.", e));
});
