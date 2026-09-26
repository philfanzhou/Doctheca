# Doctheca Admin Frontend Redesign (2026-07)

## Status

Accepted — 2026-07-20

## Background

Based on the design mockup at `prototype/admin-console-redesign.html` in the repository root, this redesign reproduces the Doctheca admin frontend's visual and interaction layers with high fidelity. This redesign **only restores the presentation layer** and does not modify any business logic, API contracts, state management, or data flow (iron rule: zero business-logic changes).

The mockup contains two sets of interfaces, Identity (identity center) and Doctheca (document library), but the implementation scope is limited to Doctheca. The Doctheca admin console logs in as the Identity bootstrap administrator through the backend authentication proxy and does not reuse the Identity management pages from the mockup.

## Design Specification Inventory (Distilled from the Mockup)

### Design Token

| Token | Value | Purpose |
|------|----|----|
| `--primary` | `#4F46E5` | Primary color (indigo, replaces the original `#2563eb`) |
| `--primary-hover` | `#4338CA` | Primary hover |
| `--primary-soft` | `#EEF2FF` | Primary soft background |
| `--primary-line` | `#C7D2FE` | Primary border |
| `--ink-0/1/2` | `#0D0F1E / #141731 / #1D2142` | Sidebar dark background, three tiers |
| `--ink-text/2/3` | `#E7E9F5 / #9BA1C4 / #5D6388` | Sidebar text, three tiers |
| `--bg` | `#F5F6F8` | Page background |
| `--surface` | `#FFFFFF` | Card background |
| `--surface-2` | `#F8F9FC` | Secondary panel background |
| `--border` | `#E5E7EF` | Main border |
| `--border-2` | `#EEF0F6` | Table row divider |
| `--text/2/3` | `#161927 / #586076 / #9AA0B6` | Text, three tiers |
| `--success` series | `#10B981 / #ECFDF5 / #A7F3D0` | Green semantics |
| `--warning` series | `#D97706 / #FFFBEB / #FDE68A` | Yellow semantics |
| `--danger` series | `#EF4444 / #FEF2F2 / #FECACA` | Red semantics |
| `--info` series | `#0EA5E9 / #F0F9FF / #BAE6FD` | Blue semantics |
| `--r-card` | `12px` | Card border radius |
| `--r-btn` | `8px` | Button border radius |
| `--r-input` | `6px` | Input border radius |
| `--shadow-hover` | `0 6px 16px -6px rgba(22,25,39,.12), 0 2px 4px -2px rgba(22,25,39,.06)` | Card hover |
| `--shadow-float` | `0 16px 40px -12px rgba(22,25,39,.18), 0 4px 10px -4px rgba(22,25,39,.08)` | Drawer/modal |
| `--ease` | `cubic-bezier(.22,.61,.36,1)` | Standard transition curve |
| `--spring` | `cubic-bezier(.34,1.35,.44,1)` | Spring transition (indicator/modal entrance) |
| `--mono` | `ui-monospace,"SF Mono","Cascadia Code","JetBrains Mono",Consolas,monospace` | Monospace font |

Font size hierarchy: page title 23px / card title 14.5px / table body 13.5px / auxiliary text 12~12.5px / table header 11.5px.

### Page Inventory and Information Architecture (doclib portion only)

```
Sidebar (dark ink gradient)
  - Brand mark "Ruo" + "Ruoyu Study Platform / Admin Console"
  - Nav (small top label doctheca)
    - Overview   (grid)
    - Document Management (file)
    - Search Test (search)
  - Sidebar-foot: after login, shows the current Identity administrator name, role, and logout entry
Topbar
  - Breadcrumb: Document Library › current page
  - env-tag "Intranet Environment" + real-time clock (updates every second)
Main (.view, 200ms blur transition)
  - Overview page / Document Management / Document Details / Search Test
```

> **System switcher (sys-switch) not shown**: the mockup's dual-system switcher demonstrates toggling between Identity ↔ Doctheca. This project only hosts Doctheca, so no switcher is needed; keeping the nav-label "doctheca" is sufficient.

### Component Patterns

- **stat-card**: 4-column grid, numbers 29px tabular-nums, hover lifts 2px + shadow-hover
- **strip** (status strip): 4-column grid, active state primary border + 3px ring
- **card**: 12px radius, 1px border, padding 24px; card-head 18px bottom margin
- **badge**: 6 color tiers (gray/green/amber/red/blue/indigo), with 5px dot + 23px height
- **chip**: 29px-high rounded pill, active state primary-soft background
- **btn**: 34px height, primary/ghost/danger/sm four tiers
- **input/select**: 34px height, focus 3px primary ring
- **switch**: 34x20 custom toggle (does not use EP el-switch default visuals, but functionally equivalent)
- **table**: header 11.5px uppercase letter-spacing, tbody row hover light background; clickable rows cursor pointer
- **drawer**: right-side 520px, spring entrance (translateX 36→0 + opacity 0→1), 200ms blur mask
- **modal**: 460px scale + translateY entrance (spring curve)
- **toast**: dark notification in the top-right corner, spring entrance
- **pager**: right-aligned, current page primary background with white text
- **upload-zone**: 1.5px dashed border, hover/drag primary-soft background
- **chart**: SVG line chart (Catmull-Rom smoothed path) + donut (stroke-dasharray drawing animation)
- **progress-track**: 5px height, linear-gradient(90deg, #818CF8, #4F46E5) fill

### Animation Inventory

- Number roll-up: `runCounters` cubic ease-out 900ms, tabular-nums
- SVG line drawing: stroke-dashoffset 1.1s ease
- SVG donut drawing: stroke-dasharray 0.9s ease, segments staggered by 0.12s
- Card hover: translateY -2px + shadow-hover, 220ms ease
- Drawer entrance: translateX 36→0 + opacity, 300ms ease
- Modal entrance: scale(.96)→1 + translateY 8→0, 260ms spring
- Toast entrance: translateX 24→0 + opacity, 300ms spring
- View switching: .view opacity 0 + blur 6px + translateY 5px, 150ms ease (leaving) → switch → restore
- Sidebar nav-indicator slide: translateY follows the current active item, 340ms spring

## Existing Project Mapping

| Mockup page | Existing project file | Change type |
|---------|------------|---------|
| Overview (pgDocOverview) | — | **New page** `OverviewPage.vue` |
| Document Management (pgDocDocs) | `views/DocManagePage.vue` | Rework (status strip + upload zone + table visuals) |
| Document Details (pgDocDetail) | — | **New page** `DocDetailPage.vue` |
| Search Test (pgDocSearch) | `views/SearchPage.vue` | Rework (card-style results + dev mode) |
| Parse Results | `views/ParseResultsPage.vue` | Repackage with the mockup design language (keep all buttons) |
| Layout shell | `App.vue` | Full redo (dark sidebar + nav-indicator + clock) |
| Global styles | `style.css` + `styles/element-variables.scss` | New design tokens; primary color `#2563eb` → `#4F46E5` |

### Data Sources for New Pages (APIs untouched)

- **Overview page**: calls the existing `listDocumentFiles(page=1, pageSize=100)` and `listDocumentParses(page=1, pageSize=100)`; aggregates by `parseStatus` on the frontend to build the status-distribution donut, buckets by `parsedAt` date to build the last-30-days parse throughput line chart, and takes the top 5 entries sorted on the frontend from `listDocumentFiles` as "Recently Uploaded". Statistics have truncation error beyond 100 samples, which is noted in the docs.
- **Document details page**: uses `getDocumentFile(id)`, showing basic file info + the parses list (with markdown/HTML/JSON/V2/Model/Layout/image buttons, behaving the same as ParseResultsPage but changed to drawer-style previews). Clicking "Re-parse" calls `parseDocumentFile`; "Start Parse" likewise.

### Present in the Mockup but Not Implemented This Time (with reasons)

| Mockup element | Reason |
|---------|------|
| Subject/grade/year metadata card (top-right of pgDocDetail) | The `DocumentFile` API has no `subject/grade/year` fields; to avoid a static facade, **this card is omitted**. If the backend later extends a `DocumentMetadata` module, it will be added back. |
| Downstream flow card (bottom-right of pgDocDetail) | OpenSearch sync status / question-bank import buttons (formerly QuestionBank, now the external repository Quaestura, ADR-0011) have no corresponding APIs; **omitted**. |
| Question-bank import toast demo | The mockup uses an `App.toastDemo(...)` mock; no corresponding API, **not implemented**. |
| System switcher sys-switch | Doctheca only consumes Identity authentication and does not host the Identity management UI; **not shown**. |
| Identity management pages | Out of scope; only the Doctheca login page and cookie session are implemented. |

### Absent from the Mockup but Kept in the Project

- **ParseResultsPage** (parse results list page): the mockup folds parse records into document details, but this project's historical version provides a standalone list entry. **Keep** this page, repackaged with the mockup design language (dark sidebar + cards + EP components + unified tokens). Deleting it would break an existing feature entry, violating "do not delete feature entries that exist in the business but not in the mockup".
- **8 minerU advanced filter parameters** (blockType/subType/pageNumber/textLevel/textFormat/parseId/documentFileId/hasImage): kept, migrated to the drawer-style advanced filter.
- **Row-expanded blockData details**: kept, migrated to the result card's "expand details" interaction.

## Technical Mapping Strategy

- Primary color `#2563eb` → `#4F46E5`, overriding `$colors.primary.base` in `element-variables.scss`
- New `src/styles/tokens.scss` centrally stores the mockup design tokens (CSS variables) for scoped styles to reference
- No chart library introduced — the mockup's SVG charts are rewritten with Vue + native SVG (same drawing-animation logic as the mockup)
- Sidebar hand-written with dark background + sliding nav-indicator; does not use the EP el-menu default light style (EP's default does not match dark styling; hand-writing is more direct)
- Topbar hand-written: sticky + backdrop-blur + clock
- Button styles hand-written as `.btn / .btn-ghost / .btn-danger / .btn-sm` (same names as the mockup), not forced onto the EP el-button default visuals
- Drawer / Modal / Toast hand-written, visually identical to the mockup; does not use the EP el-drawer/el-dialog/el-message default visuals
- Tables hand-written in the mockup's `.table-wrap > table` style, still using the v-loading directive
- Pagination hand-written in the `.pager` style, keeping EP el-pagination as the underlying pagination capability

> **Decision**: this project's ADR-001 already adopted EP and completed the migration. This redesign keeps EP as the base component library, but for the **core display components** (buttons, cards, tables, drawers, modals, toasts) it uses hand-written classes with the same names as the mockup, because the mockup's visual details (radius tiers, shadow tiers, animation curves, nav-indicator sliding, SVG drawing animations) are hard to reproduce with high fidelity via EP default tokens + scoped overrides, and forcing overrides would produce many deep selectors. EP is still used for form and pagination capabilities such as `el-input/el-select/el-pagination/el-checkbox/el-switch`, avoiding reinventing the wheel.

## Changed File Inventory

### New

| File | Purpose |
|------|------|
| `frontend/src/styles/tokens.scss` | Design Token CSS variables |
| `frontend/src/components/Sidebar.vue` | Dark sidebar (sliding nav-indicator) |
| `frontend/src/components/Topbar.vue` | Topbar (breadcrumb + clock + env-tag) |
| `frontend/src/components/ChartLine.vue` | SVG line chart (drawing animation) |
| `frontend/src/components/ChartDonut.vue` | SVG donut chart (drawing animation) |
| `frontend/src/components/AppDrawer.vue` | Generic drawer (spring entrance) |
| `frontend/src/components/AppModal.vue` | Generic modal (spring entrance) |
| `frontend/src/components/AppToast.vue` | Toast container (spring entrance) |
| `frontend/src/components/StatCard.vue` | Stat card (hover lift + number roll-up) |
| `frontend/src/components/StatusStrip.vue` | Status strip |
| `frontend/src/composables/useToast.ts` | Toast invocation composable |
| `frontend/src/composables/useCountUp.ts` | Number roll-up composable |
| `frontend/src/views/OverviewPage.vue` | Overview page |
| `frontend/src/views/DocDetailPage.vue` | Document details page |
| `frontend/src/views/ParsePreviewDrawer.vue` | Parse artifact preview drawer (Markdown/image/JSON three tabs) |
| `frontend/src/router/index.ts` | Simple hash router (#docs/:id / #search, etc.) |

### Modified

| File | Change |
|------|---------|
| `frontend/src/App.vue` | Full redo of the layout shell: dark Sidebar + Topbar + view container (200ms blur transition) |
| `frontend/src/main.ts` | Import tokens.scss |
| `frontend/src/style.css` | Keep typography helpers, remove items conflicting with tokens |
| `frontend/src/styles/element-variables.scss` | Primary color changed to `#4F46E5`, transition durations per the mockup |
| `frontend/src/views/DocManagePage.vue` | Add status strip + upload zone + rewrite table visuals |
| `frontend/src/views/ParseResultsPage.vue` | Rewrite visuals with the mockup design language |
| `frontend/src/views/SearchPage.vue` | Card-style results + dev-mode dev-block + drawer advanced filter |

### Untouched

- `frontend/src/services/docApi.ts` (API client, zero changes)
- `frontend/src/utils/format.ts` (existing helper, reused)
- `frontend/package.json` / `vite.config.ts` / `tsconfig*.json` (build config untouched)
- Any backend file

## Zero-Business-Logic-Change Self-Check List

After implementation, `git diff` must satisfy:

- [ ] `docApi.ts` has 0 lines changed
- [ ] All API endpoints (`/admin/document-files/*`, `/admin/document-parses/*`, `/admin/documents/search`) calls and parameters are fully equivalent
- [ ] `parseDocumentFile(id, modelVersion)` still passes `'vlm'` or `'pipeline'`
- [ ] `searchTest` still passes all 8 minerU filter parameters
- [ ] Polling logic (5s interval + auto-stop when parsing completes) preserved
- [ ] Upload progress callback logic preserved
- [ ] Delete confirmation (ElMessageBox) preserved
- [ ] Fetching parse records in file details still goes through `getDocumentFile(id)`, no new endpoints introduced
- [ ] Markdown / HTML / JSON / V2 / Model / Layout / image open behaviors are equivalent (only changed to drawer-style previews; the APIs called are the same)
- [ ] Export Markdown / HTML (blob download) behavior is equivalent

## Verification

1. `npm run build` (vue-tsc + vite build) with zero errors and zero warnings
2. Before/after screenshot comparison (visual consistency ≥ 90%)
3. Item-by-item regression of existing features: upload / parse trigger / polling / deletion / search / advanced filter / row expansion / export
4. `git diff` contains no API endpoint / request parameter / store logic changes
