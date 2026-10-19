import { defineConfig } from "vite";

// Library build into the web app's static assets: the Blazor editor island imports /editor/athar-editor.js, and share
// pages with a diagram load /editor/athar-mermaid.js (T120). Mermaid itself is a lazily imported chunk beside them.
export default defineConfig({
  build: {
    lib: { entry: { "athar-editor": "src/index.ts", "athar-mermaid": "src/mermaid-page.ts" }, formats: ["es"], fileName: (_, name) => `${name}.js` },
    outDir: "../Athar.Web/wwwroot/editor",
    emptyOutDir: true,
  },
});
