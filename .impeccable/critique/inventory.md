# Athar UI findings inventory (Assessment A + B, 2026-10-04) — the build checklist

Target: DESIGN.md ("The Public Record"). Line numbers refer to the code before the redesign. Shared controls live in
`src/Athar.Web/Components/Shared` (PageHeader, DataTable + SortTh + ActionsTh, RowActions + RowAction, IconAction, Seal,
ScopeChip, Timestamp, Section, FilterBar, EmptyState, LoadingState). Global rules: page titles only through PageHeader;
no MudText Typo h4/h5/h6 titles; sentence case; outlined fields (global default); every table a DataTable with SortTh
on every meaningful column; row actions through RowActions (≤2 icons, the rest in ⋮, destructive ones Danger + Confirm);
every icon-only button through IconAction; dates through Timestamp; empty states through EmptyState; loading through
LoadingState; no hard-coded colors (use --mud-palette-* / --athar-*); no inline width styles in filter bars (FilterBar).

## Cross-cutting
- Loading = bare spinner at: AuditLog:11, SpaceSettings:12, Teams:12, TokensAdmin:13, Users:12, Workspaces:13, Inbox:13, MemoryBrowser:31, MemoryDetailPage:16, Proposals:13, Search:28; WikiHome:9 and WikiPage:15 render nothing. → LoadingState.
- Empty states built three ways: MudAlert (Inbox:17, MemoryBrowser:35, Proposals:17), plain text (Workspaces:29, WikiHome:15, Search:39, TokenTable:32, MyTokens:46, CommentsPanel:27, VersionsPanel:14, WikiPage:59,77,94), none (Users, Teams, SharePanel shared-with, MemoryDetail links). → EmptyState.
- "AdminsOnly" info alerts: AuditLog:15, Teams:16, TokensAdmin:17, Users:16, SpaceSettings:16 → `AccessDenied` (lock icon + who to ask).
- Missing confirms: Deactivate Users:64, role change Users:46, KO toggle Users:54, revoke token TokenTable:28, Disconnect MyTokens:55, unshare SharePanel:18, revoke link SharePanel:50, delete comment CommentsPanel:63, discard Inbox:44, remove member SpaceSettings:74, team chip close Teams:36, workspace chip close Workspaces:67, mark confidential SpaceSettings:46 + MemoryDetailPage:67.
- Timestamps not bidi-isolated: CommentsPanel:43, VersionsPanel:24, NotificationBell:34, MemoryBrowser:55, MemoryDetailPage:93, Proposals:28, TokenTable:22-23, SharePanel:45, WikiPage:70 → Timestamp.
- "→" glyphs not mirrored: AuditLog:49 (Change header), Proposals:43 → use an arrow icon that flips in RTL (Icons.Material.Filled.ArrowForward with class `flip-rtl`) or the word.
- Inline width styles: MyTokens:24,36, AuditLog:21, SpaceSettings:39,79, Teams:37, Users:24, Workspaces:73, Inbox:29, MemoryBrowser:13,16,19,22, MemoryDetailPage:62,83, Proposals:60, Search:16, SharePanel:24.
- Selects showing raw values: MemoryBrowser:19 (status "active" before localization) → ToStringFunc; AuditLog:22-24 resource types unlocalized.
- Server AtharError messages go to snackbars unlocalized (e.g. WikiPage:225) — keep, but map known codes where a resource string exists.

## Shell (MainLayout, NotificationBell) — done by the lead
Sidebar nav, top bar search field (/ and Ctrl+K), account menu (My tokens, Language, Theme, Sign out), main landmark + skip link, dark mode, language switch, Admin › Tokens link, NotificationBell as MudMenu/popover with Esc/outside close, notification text "X mentioned you on Page" with a gap before the time, badge inside the bar.

## Auth (Account/*, OAuth/*, share pages) — done by the lead
AuthLayout with the archive photo; Login, ForgotPassword (replace form with confirmation + "Back to sign in"), ResetPassword (reset-specific invalid-link message + "Request a new link"; minimum length as help text), AcceptInvite (show expired state up front), OAuthLogin (name the client), OAuthConsent (auth layout, client header, Deny as ghost); public share pages (ShareHtml.cs, ShareEndpoints.cs) onto the same tokens, localized, `lang`/`dir`.

## Admin
### Users.razor
- h4 title → PageHeader with primary "Invite" (opens dialog; invite form today always open at :20-32).
- Table (:34-68): Name, Email, Role (inline MudSelect, applies instantly), Knowledge owner (switch, silently disabled for non-owners), Status (holds an ACTION: Deactivate red text / Reactivate). → DataTable: sortable Name, Email, Role, Status; Role as quiet Seal; Knowledge owner as Seal ("Knowledge owner"); Status as Seal (Active / Deactivated). RowActions in ⋮: Change role… (dialog or submenu, confirm), Make/Remove knowledge owner (owners only; else disabled with tooltip "Only owners can change this"), Deactivate/Reactivate (confirm). Search field (FilterBar) on name/email. Pending invitations as rows if the service exposes them (only if it does).
- Rows ~51px tall because of inline controls → plain text rows.
### Teams.razor
- Create form always open (:20-23) → PageHeader primary "New team" dialog.
- One paper per team, members as closable chips (:25-44); chip × removes instantly. → DataTable of teams (Name, Members count) with expandable detail or a members Section per team using a member table with RowActions (Remove, confirm). Add-member select (:37) → principal picker + Add button.
### Workspaces.razor
- Create workspace / create space forms always open (:19-24, :55-61) → PageHeader "New workspace" dialog + per-workspace "New space" action.
- Spaces per workspace as MudList with chips (:42-51); whole row links to settings or wiki depending on rights (unpredictable). → DataTable per workspace: Space, Classification Seal (Confidential), AI policy as icons with tooltips (not the sentence chip :48, :118-120), Members. RowActions: Open in wiki, Settings.
- Members as closable chips (:64-71), missing space "Lab Admin· Member" (:68) → member table with RowActions (Change role, Remove + confirm).
- Confidential is Warning here (:47) but Error in MemoryDetailPage:27 → one Seal (Warn kind, lock icon) everywhere.
- "Short name" jargon (:21) → "URL name" with help text.
### SpaceSettings.razor
- Bare "Workspaces" link (:8) → PageHeader breadcrumb (Workspaces › workspace › space).
- Sections :23,34,52 use h6 → Section.
- Classification (:33-49) Declassify / Mark confidential buttons + reason field (min-width 24rem :39) → PageHeader ⋮ or Section action opening a reason dialog (`ReasonDialog`).
- Members table (:53-78): Name (team icon), Role (inline MudSelect applies instantly :65), remove MudIconButton without tooltip or confirm (:74). → DataTable sortable Name, Role; Role as quiet Seal; RowActions ⋮: Change role…, Remove (confirm). Add-member select (:79) → picker + role + Add in the Section actions.
### TokensAdmin.razor + Account/TokenTable.razor + Account/MyTokens.razor
- TokenTable (:3-33): Owner (admin), Name, Scopes, "Class", Expires (also shows Revoked), Last used, actions; Revoke red text button (:28) no confirm; admin injects Make trusted local / Make external agent text buttons (TokensAdmin:31-32) stacked → rows ~75px; global reason field (TokensAdmin:24) detached from rows.
  → DataTable sortable Owner, Name, Expires, Last used; Status column (Active / Revoked / Expired seal) instead of overloading Expires; Scopes as quiet seals with readable labels (not raw "memory:write"); "Class" → "Access" seal (Trusted local = Ink, External agent = Quiet); RowActions: Revoke (Danger, confirm), ⋮ "Change access…" opening a reason dialog (remove the global reason field). Owner names/scopes must not wrap (truncate + tooltip).
- MyTokens: h4 title → PageHeader with primary "New token" (dialog: name, scopes as stacked checkboxes with readable labels + descriptions, expiry, Create); one-time secret shown with a Copy IconAction (today a read-only field :32-38); "All tokens" link (:13) only for admins → keep as PageHeader secondary action for admins. Connected apps list (:48-59) with Disconnect red text (:55) → DataTable (App, Scopes, Connected on, Last used) + Disconnect IconAction (confirm). Empty texts → EmptyState.
### AuditLog.razor
- h4 → PageHeader; CSV / JSON buttons (:27-28) → one "Export" MudMenu button in PageHeader actions.
- Filters (:19-29) with inline widths → FilterBar; resource-type values localized.
- Table (:32-52): When, Action (code), Resource (type + GUID), Actor (GUID), Credential (GUID), Claimed client (unverified), Before → after (break-all). Dates wrap; GUIDs wrap; headers wrap. → DataTable: sortable When (default descending), Action, Resource type, Actor; names instead of GUIDs where the service gives them (GUID in a tooltip + copy IconAction otherwise); one-line truncation with tooltips; Before/after as an expandable row detail (ChildRowContent) or a details dialog via an IconAction; pagination (PageSize 50). "Claimed client (unverified)" → "Client (as reported)".

## Brain
### MemoryBrowser.razor
- h4 → PageHeader. Filters (:12-26) → FilterBar; status select shows raw "active" → ToStringFunc with localized labels; "Stale shadows only" → "Only stale overrides" (check resource strings).
- Table (:39-61): Title (+Stale chip), Scope (plain text), Type, Revision, Updated. → DataTable sortable Title, Scope (ScopeChip), Type (quiet Seal), Status (new column), Revision (tabular), Updated (Timestamp); Stale as Warn Seal. RowActions: Open. Pager: Previous/Next text buttons with no total (:59-60) → "1–50 of N" with chevron IconActions (server-side paging stays).
### MemoryDetailPage.razor
- h4 → PageHeader (breadcrumb Memories › title). Scope/type/status chips (:23-26) → ScopeChip + Seals; Confidential (:27) → Warn Seal with lock.
- Edit / Archive outlined buttons (:55-56) → PageHeader actions: Edit (secondary), ⋮ Archive (confirm exists :137). Declassify / Mark confidential (:63,67) with min-width 20rem field → ⋮ items opening a reason dialog.
- Links (:74-79): raw strings, add icon without tooltip (:84) → list with remove IconAction; History (:89-98) → compact DataTable (rev, date, who, reason).
### Inbox.razor
- h4 → PageHeader ("Capture inbox"). Card per candidate (:24-46): scope select inline width (:29); "Similar" links show 8-char id prefixes (:40) → titles; Accept (filled) / Discard (red outlined :43-44, no confirm) → Accept primary, Discard IconAction (Danger, confirm). Empty alert → EmptyState.
### Proposals.razor
- h4 → PageHeader. Status chips (:26-27) → Seals; "→" (:43) mirrored; Approve / inline note / Reject (:59-61) → Approve primary, Reject secondary opening a "Reject with note" dialog; Rebase (:65) wrong refresh icon → sync icon, label "Update to latest". Evidence "page: GUID" (:50-52) → resolved title link. Empty alert → EmptyState. (Crash for knowledge owner fixed by the WikiSession change.)
### DiffView.razor — keep (+/− markers); check colors are tokens.

## Wiki
### WikiHome.razor
- Nested MaxWidth.Medium container (:11); h1 "Spaces" then Typo.h6 renders <h6> (:19) → PageHeader "Wiki", workspace groups as Section/h2; spaces as a list or DataTable (Space, Workspace, your role, AI policy icons) with Open. Empty → EmptyState.
### WikiPage.razor (+ .razor.css, PageTreeItem.razor, PageEditor.razor(.css))
- No h1: title is an underlined text field with a visible "Title" label (:99-100) → in-place editable title that renders as h1 (20px/600), field look only on hover/focus; read-only shows plain h1.
- Tree (WikiPage.razor.css:18-36): rows flex-wrap + overflow-wrap anywhere → one line with ellipsis + title tooltip; tree icon buttons 26px (:25,27) → IconAction; space name should be an h2 in the tree nav.
- Move form inline (:33-52, Move/Cancel :49-50) → "Move page" dialog.
- Trash list Restore text button (:68) → IconAction; Timestamp (:70).
- Editor toolbar (:119-130) 13 ungrouped icons with native title → IconAction (tooltip) and group with dividers; URL Insert/Cancel row (:134-136) → popover/dialog.
- Save-state chip (:109-110) → Seal (Quiet) with role=status kept.
- Avatar color:#fff (:105) and presence colors (PageEditor.razor:13) → tokens that work in both themes.
- PageEditor.razor.css: teal/grey fallbacks (:2,6,15,26,43,44,51,62), caret label #fff (:79), yellow comment anchors (:86-92) and WikiPage.razor.css:53-54 → CSS tokens (comment anchor = warning tint var).
- Phone: tree min-height 100vh makes the tree the whole first screen → below 960px tree in a drawer or tabs (Pages / Page / Comments).
- Inaccessible page calls Nav.NotFound() (:420-423) → localized not-found/no-access state inside the shell with "Back to wiki".
- Tabs Comments/Versions/Share: show comment count in the tab.
### CommentsPanel.razor
- "Comments" h2 repeats the tab (:9-10) + Show resolved switch → icon toggle in a panel toolbar.
- Separate "Mention someone" MudAutocomplete (:17-18), toggle named "None" → keep the picker but give it a proper name OR inline @ completion; Variant.Text explicit (:18,73) → remove.
- Two submit buttons "Comment on selection" / "Comment on page" (:20-21) → one primary "Comment" plus a small "Anchor to selection" toggle shown only when there is a selection. KEEP accessible names used by tests: the smoke test AT-46 tabs to the label "Add a comment" and a button named "Comment on page" — keep a button with that accessible name, or update tests/Athar.IntegrationTests/Smoke/SmokeTests.cs accordingly.
- Comment item (:43-63): author and date run together (no bdi); Edit / Delete text buttons; edit has no Cancel; delete no confirm → `CommentItem` component: avatar, name, Timestamp (relative, full tooltip), "edited" marker, pencil IconAction + ⋮ Delete (confirm); edit mode Save + Cancel.
- Reply input + Reply button always open on every thread (:72-74) → "Reply" link that expands the composer; send IconAction.
- Resolve / Reopen text buttons (:77,81) → check-circle IconAction in the thread header.
- Quote with hard-coded English curly quotes (:34) and yellow bar → blockquote styled with tokens, no glyph quotes.
- Status chip "Linking…/Orphaned" (:37-39) → Seal with icon.
### SharePanel.razor
- h3 without h2 (:12,38) → Section-like headings inside the tab (h3 fine if tab panel has a heading).
- People with access (:13-20): close MudIconButton no tooltip/confirm (:18) → rows with avatar, level Seal, remove IconAction (confirm). Empty → "Only space members".
- Share form (:21-31) Variant.Text (:22), inline width (:24) → outlined fields; Share button sentence case.
- Links (:39-53): one wrapped caption kind·password·expiry·state; Revoke red text (:50) → rows with state Seal (Active/Expired/Revoked), copy-link IconAction, revoke IconAction (confirm), truncation. Create link (:68) options behind an "Add link" disclosure; created link shown with copy IconAction (:54-60).
- Test AT-46 uses a button named "Create link" and the "Share" tab — keep those names.
### VersionsPanel.razor
- Save a snapshot text button (:10) → header IconAction; rows r#, reason chip, time·author (:16-27) → compact list with Timestamp, preview + Restore IconActions; Restore keeps its confirm (:36-45). "r12" → "Revision 12" in tooltips.
### NotificationBell.razor (lead)

## Pages
### Search.razor
- h4 → PageHeader; inline width (:16); kind filter (:17-21) → segmented sentence-case control; redundant Search button (:22) → drop (Enter submits); results (:41-54) → result items with kind icon, breadcrumb (space/scope), Timestamp, highlighted snippet; empty query → EmptyState "Type to search".
### NotFound.razor / Error.razor — localized, inside the shell, PageHeader + EmptyState with "Back to Home"; Error.razor no developer text in production.
### Home.razor — new signed-in home (lead).
### ReconnectModal.razor(.css) — English strings, blue hard-coded colors (:30,44,93,100,104,115) → tokens + localized strings.

## Detector (Assessment B) extras
- No favicon (/favicon.ico 404) — fixed by the lead.
- Notification badge clipped above the bar and 3.7:1 contrast.
- Muted MudBlazor input labels at 4.49:1 on #f8fafc (fixed by the new palette, verify).
- Touch targets < 44px on phones: tree icon buttons 26px, links 24px tall, selects 32px — IconAction Size.Medium on phones or a larger hit area.
- Unnamed MudInput adornment button (tabindex -1) on the wiki page.
