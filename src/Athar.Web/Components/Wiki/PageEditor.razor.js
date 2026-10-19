import { mount } from "/editor/athar-editor.js";

// The module owns its cleanup: it destroys the provider when its element leaves the DOM or the page hides,
// whatever happens to the Blazor circuit (docs/03 §8).
export function create(el, dotnet, opts) {
  const call = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(() => {});
  const island = mount(el, {
    ...opts,
    pageHref: (id) => opts.pageHref.replace("{id}", id),
    token: () => dotnet.invokeMethodAsync("Ticket"),
    onSaveState: (state, revision) => call("SaveState", state, revision ?? null),
    onPresence: (people) => call("Presence", people),
    searchPages: async (query) => (await dotnet.invokeMethodAsync("SearchPages", query).catch(() => [])) ?? [],
    searchPeople: async (query) => (await dotnet.invokeMethodAsync("SearchPeople", query).catch(() => [])) ?? [],
    onTool: (command) => call("Tool", command),
  });
  let destroyed = false;
  const observer = new MutationObserver(() => el.isConnected || destroy());
  function destroy() {
    if (destroyed) return;
    destroyed = true;
    observer.disconnect();
    removeEventListener("pagehide", destroy);
    island.destroy();
  }
  observer.observe(document.body, { childList: true, subtree: true });
  addEventListener("pagehide", destroy);
  return {
    destroy,
    run: (command, arg) => island.run(command, arg ?? undefined),
    commentSelection: () => island.commentSelection(),
    clearCommentSelection: () => island.clearCommentSelection(),
    setResolvedThreads: (ids) => island.setResolvedThreads(ids),
    focusThread: (id) => island.focusThread(id),
    setReadMode: (on) => island.setReadMode(on),
    outline: () => island.outline(),
    scrollToHeading: (pos) => island.scrollToHeading(pos),
    wordCount: () => island.wordCount(),
  };
}
