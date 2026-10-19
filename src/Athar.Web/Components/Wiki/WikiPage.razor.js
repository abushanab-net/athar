// Drag and drop in the page tree. The browser drags and shows where a page will land; .NET hears only the drop
// (PageService.DropAsync checks rights and cycles). Pointer in the top quarter of a row: before it; bottom quarter:
// after it (both as its sibling, which also moves a page out of its parent); otherwise inside it, as the last child.
// The ⋮ menu (move up, down, to) stays the keyboard way to do the same.
export function attachTreeDnd(tree, dotnet) {
  if (tree.dataset.dnd) return;
  tree.dataset.dnd = "1";
  const zones = ["drop-before", "drop-after", "drop-inside"];
  let dragged = null; // { id, li }
  const rowOf = (e) => e.target.closest?.(".tree-row[data-page-id]");
  const zoneOf = (row, e) => {
    const box = row.getBoundingClientRect();
    const y = (e.clientY - box.top) / box.height;
    return y < 0.25 ? "before" : y > 0.75 ? "after" : "inside";
  };
  const unmark = () => tree.querySelectorAll(".drop-before, .drop-after, .drop-inside").forEach((r) => r.classList.remove(...zones));
  const target = (e) => {
    const row = rowOf(e);
    // Not onto itself or into its own subtree.
    return dragged && row && !dragged.li.contains(row) ? row : null;
  };

  tree.addEventListener("dragstart", (e) => {
    const row = rowOf(e);
    if (!row || row.getAttribute("draggable") !== "true") return;
    dragged = { id: row.dataset.pageId, li: row.closest("li") };
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", row.dataset.pageId);
    row.classList.add("dragging");
  });
  tree.addEventListener("dragover", (e) => {
    const row = target(e);
    if (!row) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    const zone = "drop-" + zoneOf(row, e);
    if (!row.classList.contains(zone)) {
      unmark();
      row.classList.add(zone);
    }
  });
  tree.addEventListener("dragleave", (e) => {
    const row = rowOf(e);
    if (row && !row.contains(e.relatedTarget)) row.classList.remove(...zones);
  });
  tree.addEventListener("drop", (e) => {
    const row = target(e);
    if (!row) return;
    e.preventDefault();
    const where = zoneOf(row, e);
    const id = dragged.id;
    end();
    dotnet.invokeMethodAsync("DropPage", id, row.dataset.pageId, where).catch(() => {});
  });
  const end = () => {
    unmark();
    tree.querySelectorAll(".dragging").forEach((r) => r.classList.remove("dragging"));
    dragged = null;
  };
  tree.addEventListener("dragend", end);
}
