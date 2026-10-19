# @athar/editor

Tiptap 3 + Yjs editor island. `npm run build` writes `src/Athar.Web/wwwroot/editor/athar-editor.js` (gitignored; `dotnet build` of `Athar.Web` runs it when the sources change). It exports `mount(el, { url, page, token, user, onSaveState, onPresence, searchPages, pageHref }) → { editor, provider, run, destroy }`; the Blazor wrapper is `src/Athar.Web/Components/Wiki/PageEditor.razor`.

Try it: `dotnet run --project src/Athar.AppHost`, open `https://localhost:7100/wiki`, and pick who you are with **View as** (Development only, until sign-in). Open the same page as two users in two browsers to see live cursors and presence.
