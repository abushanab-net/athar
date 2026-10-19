---
name: athar-blazor-ui
description: Rules for Blazor and MudBlazor UI in Athar, including the Tiptap/Yjs editor island, JS interop, RTL and accessibility. Use when building or changing any page or component in src/Athar.Web/Components or src/editor.
---

# Blazor UI rules

- **Render modes:** the app is `InteractiveServer`. Public share pages (`/s/{token}`) use **static SSR only**: no interactivity and no circuit.
- **MudBlazor first.** Use Mud components and theme tokens. Don't add a second component library. Configure light and dark themes in `AtharTheme.cs`.
- **Editor island** (`PageEditor.razor` + `src/editor`):
  - Render it with `@key="PageId"`. When the page ID changes, dispose the old island and mount a new one.
  - Mount in `OnAfterRenderAsync(firstRender)`, then `ShouldRender => false`.
  - **The JS module owns its cleanup.** A `MutationObserver` removal hook and `pagehide` destroy the provider. .NET disposal is best effort and catches `JSDisconnectedException`.
  - When the circuit reconnects, the editor keeps its own WebSocket. Never pass document content through SignalR.
  - Keep interop messages small: quotes up to 2 KB; send anything larger by ID.
  - .NET → JS: `setReadOnly`, `focusThread`. JS → .NET: `onSelectionForComment(range, quote)`, `onPresence`, `onSaveState(synced|saved, revision)`.
  - Comment anchors are created through the server's anchor operation, never by the client editing the document.
- **Comments panel:** a Blazor component that loads threads over the API. It highlights the anchor by calling `editor.focusThread(id)`.
- **RTL and Arabic:**
  - Set `dir="auto"` on content blocks.
  - Choose the layout direction from the user's locale (`<MudRTLProvider RightToLeft=…>`).
  - Never hard-code left or right: use logical CSS (`margin-inline-start`).
  - Keep all strings in `.resx` files (en, ar).
- **Accessibility:** every icon button gets an `aria-label`, focus stays visible, and dialogs trap focus. Smoke tests use roles and labels as selectors, not CSS classes.
- **State:** use per-page scoped services. Don't add a global store. Update notifications live through a scoped `NotificationHub` circuit service.
