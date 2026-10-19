---
target: Athar portal (full)
total_score: 25
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 4
timestamp: 2026-10-12T06-48-09Z
slug: src-athar-web-components
---
Method: dual-agent (A: design review · B: detector + browser)

| # | Heuristic | Score | Key issue |
|---|---|---|---|
| 1 | Visibility of system status | 3 | /review lands on an empty Inbox while Conflicts has items; reopened threads jump to the end |
| 2 | Match system / real world | 3 | Nav "Connect your agent" opens a page titled "Agents"; "Organization tokens" vs "Tokens" |
| 3 | User control and freedom | 3 | Esc/Skip in tours, confirms on delete; a stray scrim click ends a tour |
| 4 | Consistency and standards | 2 | Three tab styles, two tooltip systems (64 native title=), two identical "?" icons, mixed button variants |
| 5 | Error prevention | 3 | Confirms and disabled empty submits in place |
| 6 | Recognition over recall | 2 | Share tab hidden behind tab-strip scroll arrows; native-title tooltips never show on focus |
| 7 | Flexibility and efficiency | 2 | Only "/" shortcut; no sidebar rail; three auto-starting tours |
| 8 | Aesthetic and minimalist | 2 | /help is a 6,273px wall; /agents a stack of equal boxes; comment composer shows two fields |
| 9 | Error recovery | 3 | Snackbars keep input |
| 10 | Help and documentation | 2 | Help deep-linked but English-only, no index, no search |
| **Total** | | **25/40** | Acceptable |

## Priority issues
- [P1] Wiki side panel: 4 tabs + count overflow at 320-360px, MudTabs shows scroll arrows and hides Share; order is Comments first (WikiPage.razor:330-361, consts :859). Fix: Outline, Comments, Versions; drop Share tab (page bar has Share).
- [P1] Comments panel misses the target card style: timeline connectors, top composer with separate Mention field, Reply link + paper plane, Resolve outside the ⋮, "Open0" filters (CommentsPanel/CommentThread/CommentItem).
- [P1] ⋮ flicker on open threads: MudTooltip wraps MudMenu (RowActions.razor:12-21); tooltip is not pointer-transparent and is clamped/flipped at the viewport edge, so it lands under the pointer and toggles enter/leave over SignalR; CommentsPanel reloads on every parameter set (lambdas from WikiPage) amplifying re-renders.
- [P1] /agents scrolls sideways at 390px (tabs + pre lack min-width:0).
- [P2] /help 6,273px desktop / 9,827px phone, 71 over-long lines, two-link TOC, admin action inside text.
- [P2] Navigation: Help in the Agents group, Search duplicates the top bar, no rail mode, Workspaces under Admin at /workspaces, /review redirect ignores counts, two "?" icons.
- [P2] Tooltips: two systems; edge clipping ("Comme / actions"); no nowrap.
- [P2] Tour: Material text buttons for Skip/Learn more, text-only progress, three auto-starts, scrim click ends tour.
- [P2] Mobile: search collapses to "Sea…", 44px targets only inside wiki, wiki tree button has no "Pages" label, Review tab strip cut off.
- [P3] Detector: 23 font-size drift (13px / 11px / 10px undocumented steps), 2 radius (12px page card, 3px), 2 side-tab (blockquote, false positive).

## Persona red flags
- Alex: no rail, three auto tours, no bulk review actions.
- Sam: native title tooltips not on focus; comment filters are aria-pressed buttons not tabs; resolved-thread 0.72 opacity drops muted text below AA.
- Casey: search field collapses, 26-30px row actions, comments far below the editor.

## Detector
CLI 27 advisory findings (23 font-size, 2 radius, 2 side-tab FP). Overlay injected on 8 pages: line-length ×71 on /help, text-occlusion on a dense outlined input on /memories; shell findings are MudBlazor framework transitions (FP). No page-level horizontal overflow except /agents (A); all icon-only buttons have aria-labels.
