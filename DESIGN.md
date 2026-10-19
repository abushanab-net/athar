---
name: Athar
description: A modern public record for a team's knowledge and its agents' memory. Ruled ledgers, clear forms, a seal for every decision.
colors:
  registry-ink: "#14213d"
  ink-navy: "#1b2a4a"
  ink-navy-hover: "#101a30"
  cinnabar-seal: "#c2410c"
  cool-ground: "#f6f7f9"
  record-white: "#ffffff"
  ledger-fill: "#eef1f5"
  hairline: "#dfe3ea"
  field-stroke: "#c6ccd6"
  slate-muted: "#5b6475"
  danger: "#b42318"
  success: "#1f7a4d"
  warning: "#b45309"
  night-ground: "#0f1420"
  night-surface: "#161d2b"
  night-fill: "#1d2636"
  night-hairline: "#273042"
  night-ink: "#e7eaf0"
  night-muted: "#98a2b3"
  night-primary: "#a9bde6"
  night-seal: "#f07a45"
typography:
  display:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "clamp(2rem, 4vw, 2.75rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.01em"
  headline:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "20px"
    fontWeight: 600
    lineHeight: 1.3
  title:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: 1.4
  body:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
  reading:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "0.9375rem"
    fontWeight: 400
    lineHeight: 1.7
  mono:
    fontFamily: "ui-monospace, Cascadia Mono, Consolas, monospace"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Public Sans, Noto Sans Arabic, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 500
    lineHeight: 1.4
    letterSpacing: "0.02em"
rounded:
  xs: "2px"
  sm: "4px"
  md: "6px"
  lg: "8px"
  pill: "999px"
spacing:
  "1": "4px"
  "2": "8px"
  "3": "12px"
  "4": "16px"
  "6": "24px"
  "8": "32px"
  "12": "48px"
components:
  button-primary:
    backgroundColor: "{colors.ink-navy}"
    textColor: "{colors.record-white}"
    rounded: "{rounded.md}"
    padding: "7px 14px"
    height: "32px"
  button-primary-hover:
    backgroundColor: "{colors.ink-navy-hover}"
  button-secondary:
    backgroundColor: "{colors.record-white}"
    textColor: "{colors.registry-ink}"
    rounded: "{rounded.md}"
    padding: "7px 14px"
    height: "32px"
  button-ghost:
    textColor: "{colors.ink-navy}"
    rounded: "{rounded.md}"
    padding: "7px 10px"
  button-danger:
    textColor: "{colors.danger}"
    rounded: "{rounded.md}"
    padding: "7px 14px"
  icon-action:
    textColor: "{colors.slate-muted}"
    rounded: "{rounded.md}"
    size: "30px"
  input-field:
    backgroundColor: "{colors.record-white}"
    textColor: "{colors.registry-ink}"
    rounded: "{rounded.md}"
    padding: "6px 10px"
    height: "34px"
  seal-chip:
    textColor: "{colors.cinnabar-seal}"
    rounded: "{rounded.pill}"
    padding: "1px 8px"
  nav-item-active:
    backgroundColor: "{colors.ledger-fill}"
    textColor: "{colors.registry-ink}"
    rounded: "{rounded.md}"
---

# Design System: Athar

## Overview

**Creative North Star: "The Public Record"**

Athar is where a company's knowledge is written down and its decisions are approved. The interface behaves like a well-run modern records office: calm ink on cool white, ruled ledgers that are easy to scan, plain forms with labels above their fields, and a small cinnabar seal wherever something has been approved, proposed or shadowed. Nothing is decorative. Trust comes from precision: aligned columns, tabular figures, one type scale and one spacing rhythm on every page.

The system is compact. Engineers use it all day next to their editor, so body text is 14px, page titles are 20px, rows are dense, and actions are icons with tooltips rather than rows of labelled buttons. Arabic and English are equal: every surface is checked in RTL and LTR, and both scripts get a first-class face.

The previous brain-teal Material look (teal app bar, uppercase navigation, large Material headings, underlined inputs, text-button row actions) is the anti-reference.

**Key Characteristics:**
- Ink navy and cool white, with cinnabar reserved for seals.
- Ledger tables: hairline rules, sortable columns, icon row actions and a ⋮ menu.
- A left sidebar for navigation; a quiet 56px top bar for search, notifications and the account.
- 14px body, 20px page titles, 4px spacing grid.
- Flat surfaces separated by hairlines; shadows only on floating layers.

## Colors

Restrained: cool neutrals carry the page, ink navy carries action, and one cinnabar accent marks decisions.

### Primary
- **Ink Navy** (`ink-navy`): primary buttons, the active navigation marker, links, focus rings, selected states. Hover deepens to `ink-navy-hover`.

### Secondary
- **Cinnabar Seal** (`cinnabar-seal`): approval seals, badge counts in navigation and the bell, and the knowledge lines of the Athar mark (an open book in ink navy whose pages flow into cinnabar lines and nodes; `img/athar-mark.svg`, `img/athar-mark-dark.svg` for the dark shell). Never a button fill, never a scope label and never body text.

### Neutral
- **Registry Ink** (`registry-ink`): all body and heading text.
- **Slate Muted** (`slate-muted`): secondary text, table headers, timestamps, idle icons.
- **Cool Ground** (`cool-ground`): the page background behind content.
- **Record White** (`record-white`): surfaces, tables, forms, the sidebar.
- **Ledger Fill** (`ledger-fill`): row hover, the active navigation item, selected rows, quiet chips.
- **Hairline** (`hairline`): table rules, dividers, panel borders.
- **Field Stroke** (`field-stroke`): input and secondary-button borders (stronger than hairline so fields read as fields).

### Semantic
- **Danger** (`danger`), **Success** (`success`), **Warning** (`warning`): validation, destructive actions and alerts only, always paired with an icon or text so color is never the only signal.

### Dark theme
The night palette (`night-*`) mirrors every role: `night-ground` page, `night-surface` panels, `night-fill` hover and selection, `night-hairline` rules, `night-ink` text, `night-muted` secondary text, `night-primary` actions (with navy text on filled buttons) and `night-seal` for seals.

### Named Rules
**The Seal Rule.** Cinnabar appears only on decision seals, counts and the mark, under 5% of any screen. A cinnabar button or scope label is a bug.

**The Ink Rule.** There is one action color. Primary, links and focus all use ink navy; nothing else competes.

## Typography

**Display and UI Font:** Public Sans (with system-ui, sans-serif)
**Arabic Font:** Noto Sans Arabic, listed in every stack so mixed-direction text falls through cleanly.

**Character:** Public Sans was drawn for government services: neutral, legible and serious without being cold. Noto Sans Arabic matches its weight and x-height, so a bilingual page reads as one voice.

### Hierarchy
- **Display** (700, `clamp(2rem, 4vw, 2.75rem)`, 1.1): the public page hero only.
- **Headline** (600, 20px, 1.3): the page title in every `PageHeader`. One per page.
- **Title** (600, 16px, 1.4): section and panel headings.
- **Body** (400, 14px, 1.5): everything else, including table cells. Reading text in the wiki editor is 15px with a 70ch measure.
- **Label** (500, 12px, 0.02em): field labels, table headers, captions, chip text. Sentence case, never uppercase buttons.
- **Reading** (400, 15px, 1.7): body text inside the wiki editor only, with a 70ch measure.
- **Mono** (400, 14px): code, audit action names and diffs only.

### Named Rules
**The One Scale Rule.** Only these five sizes exist (plus the 15px reading size inside the editor). A page that needs another size is wrong.

**The No-Caps Rule.** Buttons, tabs and navigation use sentence case. Uppercase is not emphasis.

## Layout

- **Shell:** a 240px left sidebar (collapsible to a 64px icon rail; a drawer below 960px) and a 56px top bar with global search, notifications and the account menu. Content scrolls; the shell stays.
- **Content:** a max width of 1200px for management pages and a full-width three-pane layout for the wiki (page tree, page, comments/versions/share), which opts out of the max width with the `athar-full-bleed` class. Gutters are 24px on desktop and 16px on phones.
- **Signed-out pages:** sign-in, password reset, invitations and OAuth use the auth layout: the archive photograph (`img/auth-archive.webp`) beside a 380px form column with the mark and a language switch; below 900px the photograph becomes a 180px band. The public page before sign-in (`/welcome`) uses the same tokens with a centered 1200px column.
- **Page header:** every page starts with the same `PageHeader`: optional breadcrumb, a 20px title, an optional one-line description, and the page's actions aligned to the end. 24px below it.
- **Rhythm:** a 4px grid. Common steps are 8px inside controls, 12px between related items, 16px inside panels, 24px between sections.
- **Single-line text:** titles, names and emails truncate with an ellipsis and a tooltip instead of wrapping inside tables and lists.
- **Responsive:** tables scroll horizontally inside their own container with the actions column pinned to the end; filter bars wrap; below 1200px the wiki's side panel moves under the page, and below 960px the page tree becomes a drawer opened by a "Pages" button.

## Elevation & Depth

Flat by default. Surfaces separate by hairlines and the ground/surface step, not by shadows.

### Shadow Vocabulary
- **Floating** (`box-shadow: 0 8px 24px rgb(20 33 61 / 0.14)`): menus, popovers, autocomplete lists, dialogs. Nothing that sits in the page flow.

### Named Rules
**The Flat Record Rule.** Cards, tables and panels have no shadow. If something floats above the page, it gets the one floating shadow.

## Shapes

Gently squared: 2px on tiny inline marks (search highlights, live-cursor labels), 6px corners on controls and panels, 8px on dialogs and the sign-in panel, 4px on chips inside dense tables, and fully round seals. Borders are 1px hairlines; inputs use the stronger field stroke. Seals are circles or pills drawn with a 1.5px cinnabar stroke on a transparent fill, like a stamp.

## Components

### Buttons
- **Shape:** gently squared (6px), 32px tall, 14px medium text, sentence case.
- **Primary:** ink navy fill, white text. One per view.
- **Secondary:** white fill, field-stroke border, ink text.
- **Ghost:** no fill, ink navy text, for low-emphasis actions like Cancel.
- **Danger:** danger text with a pale danger border; destructive actions always confirm.
- **Hover / Focus:** hover deepens the fill or adds the ledger fill; focus shows a 2px ink navy ring offset by 2px.

### Icon actions
- **Style:** 30px square, transparent, slate icon (20px), ledger fill on hover, ink navy focus ring; 44px hit areas on touch screens.
- **Rule:** every icon action has a tooltip and an accessible name. Rows show up to two icon actions inline; anything more goes into a ⋮ menu.

### Chips and seals
- **Seal:** a pill with a 1.5px cinnabar stroke and cinnabar text, for decisions only: Approved, Knowledge owner's approval. Proposed and pending use the ink navy stroke.
- **Scope:** an ink outline with the scope's icon (Company, Team, Project); Personal is quiet. Scope is a label, not a decision.
- **Warn:** a tinted flag without a border, in the warning color, for states that need attention (Confidential, Stale, Deactivated, Revoked, Expired). The normal state (Active) is plain muted text, not a seal.
- **Quiet chip:** ledger fill with ink text for neutral labels (type, scope when not a decision).

### Cards / Containers
- **Corner Style:** 6px.
- **Background:** record white on the cool ground.
- **Shadow Strategy:** none (see Elevation).
- **Border:** 1px hairline.
- **Internal Padding:** 16px.

### Inputs / Fields
- **Style:** outlined, record white fill, 1px field stroke, 6px corners, dense (about 38px). The label sits in the field's top border (MudBlazor's shrunk outlined label) in the label style; static account forms put the same label directly above the field.
- **Focus:** the border turns ink navy with a 2px ink navy ring.
- **Error / Disabled:** a danger border and a message below with an icon; disabled fields use the ledger fill and muted text.

### Navigation
- **Sidebar:** record white, hairline end border, the mark and "Athar" at the top, then grouped items (Home, Wiki, Search; Brain; Manage) with 20px line icons and 14px labels. The active item gets the ledger fill, 600 weight and an ink navy icon (no colored edge bar). Counts are small cinnabar pills.
- **Top bar:** a wide search field, the notification bell and the account menu (tokens, language, sign out). No primary navigation in the top bar.
- **Phone:** the sidebar becomes a drawer opened from a menu button in the top bar.

### Ledger table (signature component)
Hairline rules between rows, no zebra stripes, a slate 12px header row with sort arrows on every sortable column, tabular figures, the ledger fill on hover, truncation with tooltips, and a final actions column (icons plus ⋮). Empty and loading states sit inside the table frame.

## Do's and Don'ts

### Do:
- **Do** build every page from the shared controls: `PageHeader`, `DataTable` + `SortTh`, `RowActions`, `IconAction`, `Seal`/`ScopeChip`, `Section`, `FilterBar`, `Timestamp`, `EmptyState`, `LoadingState`, `AccessDenied`, `ReasonDialog`, `LanguageSwitch` (static forms use the `.athar-field` pattern).
- **Do** keep page titles at 20px and body text at 14px.
- **Do** give every icon-only control a tooltip and an accessible name.
- **Do** make every table column sortable unless sorting is meaningless for it.
- **Do** check every screen in Arabic (RTL) as well as English.

### Don't:
- **Don't** use cinnabar for buttons, links, scope labels or body text (The Seal Rule).
- **Don't** stamp the normal state: Active is plain text; seals mark decisions and flags mark exceptions.
- **Don't** put primary navigation in the top bar or use uppercase labels.
- **Don't** put text buttons in table rows; use icon actions and the ⋮ menu.
- **Don't** add shadows to cards, tables or panels.
- **Don't** use underlined (Material "text" variant) inputs; fields are outlined.
- **Don't** bring back the teal app bar or the Material heading sizes.
