---
target: Athar portal (full)
total_score: 24
max_score: 40
na_heuristics: 
p0_count: 1
p1_count: 4
timestamp: 2026-10-10T14-14-49Z
slug: src-athar-web-components
---
Method: dual-agent (A: design review · B: detector + browser)

| # | Heuristic | Score | Key issue |
|---|---|---|---|
| 1 | Visibility of system status | 2 | Sidebar counts recomputed only on navigation; wiki tree/comments/versions are snapshots |
| 2 | Match system / real world | 3 | /inbox vs "Capture inbox"; Shadow / Rebase jargon |
| 3 | User control and freedom | 3 | Confirms good; no follow-through links after accept/approve |
| 4 | Consistency and standards | 2 | ⋮ menus lack tooltips; /admin URLs for member pages; two space-settings surfaces |
| 5 | Error prevention | 3 | Confirms and reason dialogs in place |
| 6 | Recognition over recall | 2 | /connect buried in account menu; empty space Overview |
| 7 | Flexibility and efficiency | 2 | Only "/" shortcut; no bulk inbox actions |
| 8 | Aesthetic and minimalist | 3 | Wiki card shadow/12px radius; cinnabar Approved seal on every memory row |
| 9 | Error recovery | 3 | Discard shows "Saved"; server messages surfaced |
| 10 | Help and documentation | 1 | No tour, little inline help |
| **Total** | | **24/40** | Acceptable |

Priority issues
- [P0] Non-page notifications (proposals, decisions, conflicts, stale shadows) have no target (NotificationService.cs:160).
- [P1] No UI for conflicts (FR-MM-08), projects + GitHub sources (FR-PJ-01, FR-KS-01/03), skills (FR-SK-01), capture sessions (FR-AG-04), notifications list.
- [P1] Stale shell counts (MainLayout.razor:118) and snapshot wiki panels.
- [P1] Connect hidden in the account menu; no per-harness tabs; no claude.ai/Desktop OAuth path.
- [P1] Search placeholder promises sources it cannot return.
- [P2] /admin/workspaces shown to members; ⋮ menus without tooltips; cinnabar overuse; wiki card shadow+radius; empty-state dead ends.
- [P3] /Error casing; Active stamped as seal; detector font-size drift (WikiPage.razor.css:323-362 raw px).

Detector: 24 CSS findings (20 font-size drift, 2 radius, 2 side-tab = blockquote false positives); browser overlay: shell-only findings, line-length on /connect and /search snippets; 0 console errors, 0 unlabeled icon controls, no page overflow at 390px.
