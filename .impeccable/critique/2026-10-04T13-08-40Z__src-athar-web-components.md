---
target: Athar web UI (all pages)
total_score: 14
max_score: 40
na_heuristics: 
p0_count: 3
p1_count: 2
timestamp: 2026-10-04T13-08-40Z
slug: src-athar-web-components
---
Method: dual-agent (A: design review · B: detector + browser evidence)

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 2 | Bare spinners on 11 pages; snackbar-only feedback; unreadable crash bar |
| 2 | Match System / Real World | 2 | GUIDs in Audit, raw scope ids, id-prefix "similar" links, jargon (Class, Rebase, Stale shadows) |
| 3 | User Control and Freedom | 1 | ~13 destructive/permission actions without confirm; no Cancel on comment edit |
| 4 | Consistency and Standards | 1 | 8 type sizes; 3 empty-state styles; mixed text/icon actions; share page is another world |
| 5 | Error Prevention | 1 | No confirms; reason field detached from row actions; expired invite shows the form |
| 6 | Recognition Rather Than Recall | 2 | Detached reason field; separate mention picker; id prefixes |
| 7 | Flexibility and Efficiency | 1 | No sortable columns anywhere; no shortcuts; no search in Users/Tokens/Audit |
| 8 | Aesthetic and Minimalist Design | 1 | Stock Material: uppercase buttons, shadows on every block, reply fields always open |
| 9 | Error Recovery | 1 | English-only, illegible crash bar; bare NotFound; raw server messages |
| 10 | Help and Documentation | 2 | Good inline help on tokens/audit/policy; none for scopes, disclosure, proposal kinds |
| **Total** | | **14/40** | **Poor** |

## Design Specificity Verdict
Stock MudBlazor 8 Material with defaults left in (teal app bar, uppercase text buttons, 34px h4 titles, underlined inputs, shadowed papers). Nothing visual says governed memory, seal or record; product truths (scope, approval, confidentiality, save state) are buried as grey chips. Deterministic scan: the CLI does not scan .razor; 24 findings in CSS (19 design-system colors, 2 side-tab, 2 radius; reconnect/app.css mostly template styles). Browser detector: low-contrast notification badge (3.7:1) and crash bar (1.0:1), skipped heading on /wiki (h1→h6), cramped tree row/chip; MudTabs text-overflow and overlay occlusion are false positives.

## Priority Issues
- **[P0] /memories and /proposals crash the circuit** (WikiSession resolving on the circuit DbContext while the page queries it). Fix: WikiSession uses its own scope and one cached task.
- **[P0] Shell**: primary navigation is 8 uppercase text buttons in a teal bar (12 controls for admins); no main landmark, no skip link; no language switch. Fix: sidebar AppShell, top bar search/bell/account menu.
- **[P0] Visual system**: teal palette, 34px titles, underlined inputs, uppercase buttons, shadows, fonts never loaded, no favicon. Fix: Registry theme, PageHeader, outlined fields, self-hosted Public Sans/Noto Sans Arabic, mark + favicon.
- **[P1] Tables**: no column sorts anywhere; text-button row actions (Revoke, Deactivate, Disconnect, Make trusted local); stacked phone rows without labels; Audit shows GUIDs and wraps. Fix: DataTable + SortTh + RowActions with confirms; horizontal scroll on phones.
- **[P1] Comments**: every action a text button, reply composer open on every thread, separate mention picker, two submit buttons, no Cancel/confirm. Fix: CommentItem with icon actions.

## Persona Red Flags
**Alex (power user):** no sorting, no shortcuts, no search in Users/Tokens, pager without totals, 5 unconfirmed reloading clicks to revoke 5 tokens, knowledge-owner proposals page crashes.
**Sam (keyboard/screen reader):** no main landmark or skip link; wiki pages have no h1; autocomplete toggles announce "None"; notification panel ignores Esc and focus; icon buttons without tooltips; destructive actions without confirm.
**Arabic-speaking engineer:** no UI language switch; share pages always lang=en LTR; timestamps not isolated ("3:00 2026/10/1 ص · Qa Member"); "→" arrows not mirrored; English curly quotes; server errors in English; Noto Sans Arabic not loaded.

## Minor Observations
Inline min-width styles across filter bars; NotificationBell text runs together; Workspaces chip loses a space ("Lab Admin· Member"); AI-policy sentence crammed into a chip; raw select values ("active", resource types); "Capture inbox" vs Inbox naming; NotFound is a bare English h3.

## Questions to Consider
- Should Wiki home stay the landing page, or become the signed-in Home that surfaces inbox, proposals and recent pages?
- Do create forms need to be open on every admin page, or can "New …" dialogs keep pages calm?
- Can comments anchor to the selection automatically, removing the second submit button?
