# Doctheca Admin Frontend Specification

## 1. Overview

Doctheca Admin is the management back-office frontend of the document library, used by local administrators to upload documents, trigger parsing (StructaDoc pipeline, ADR-0009), view parse results and test retrieval.

- Tech stack: Vue 3.5 + TypeScript + Vite + Element Plus (form/pagination controls) + a hand-written SCSS design-token system (presentation-layer components)
- Deployment: statically hosted by the Doctheca backend Host (`wwwroot/`), same-origin with the backend, no separate deployment needed
- Access: HTTP `:5012`

## 2. Dependency Policy

**Element Plus serves as the form/pagination UI library (introduced per ADR-001); presentation-layer components (button / card / table / drawer / modal / toast / sidebar / topbar, etc.) are implemented by hand according to the 2026-07-20 redesign mockup.**

Migration mandate: ADR-001 + Redesign-2026-07-Admin-Console.md (`docs/overview/`).

| Dependency | Purpose | Necessity |
|------|------|--------|
| `vue` | Framework (3.5) | Required |
| `typescript` | Types | Required |
| `vite` | Build | Required |
| `axios` | HTTP requests | Required |
| `element-plus` | Form/pagination controls (ElInput/ElSelect/ElInputNumber/ElPagination/ElSwitch/ElCheckbox/ElMessageBox) | Required |
| `element-plus/theme-chalk` | SCSS theme (primary-color token mirror) | Required |
| `sass` | SCSS compilation | Required |
| `unplugin-auto-import` / `unplugin-vue-components` | On-demand auto-import of EP components | Required |

**Not among the dependencies:**
- Other UI libraries (Naive / Arco / Ant Design Vue / Vuetify)
- Chart libraries (SVG line/donut charts are drawn natively by `ChartLine.vue` / `ChartDonut.vue`)
- `marked` (parse-result Markdown is shown verbatim in a `<pre>`)
- `@element-plus/icons-vue` (replaced by the self-maintained `utils/icons.ts`, 27 linear SVG icons)

## 3. Access Control

The Doctheca admin console logs in with the SignaCore bootstrap administrator account:

- On startup the app first calls `GET /admin/auth/session`; a valid admin session loads the admin UI, otherwise the login page is shown.
- The login page calls `POST /admin/auth/login` and does not prefill or hardcode the admin username/password.
- Access and refresh tokens are stored only by the backend in HttpOnly, SameSite=Strict cookies; the frontend never reads tokens and never writes them to localStorage/sessionStorage.
- When the shared Axios client hits a 401 on an admin API, it calls `/admin/auth/refresh` automatically only once, and concurrent requests share the same refresh promise; on refresh failure it returns to the login page.
- 403 shows "no admin permission" and does not retry automatically.
- Logout calls `/admin/auth/logout`; the backend revokes the refresh token and clears the cookies, and the frontend immediately returns to the login page.
- Static files and the login page load anonymously, avoiding the "cannot load the login page without being logged in" deadlock.

> The backend validates the Identity JWT on login, refresh and every admin API request; only `role=admin` can establish and use an admin session.

## 4. Page Structure

### 4.1 Overall Layout (App.vue)

```
┌────────────────────────────────────────────┐
│ Sidebar  │ Topbar (breadcrumb + clock + env-tag) │
│ Dark sidebar ├────────────────────────────────┤
│  - Brand  │  #view (dynamic views)          │
│  - Overview │   - OverviewPage (#overview)    │
│  - Doc mgmt │   - DocManagePage (#docs)       │
│  - Results │   - DocDetailPage (#detail/:id) │
│  - Search  │   - ParseResultsPage (#results) │
│  - Foot   │   - SearchPage (#search)        │
└────────────────────────────────────────────┘
```

- **Routing**: hash routing (`#overview` / `#docs` / `#detail/:id` / `#results` / `#search`), parsed by `App.vue`; vue-router is not introduced
- **View-switch transition**: on switch the `#view` container gets a `.leaving` class that triggers a 150ms blur + translate transition, then the content is swapped when it ends
- **Nav items**: Overview, Doc Management, Parse Results, Search Test (4 items); in the `detail` state the sidebar highlights "Doc Management"
- **Sidebar indicator**: `Sidebar.vue` has a built-in `.nav-indicator` slider that follows the active switch with a 0.22s spring translateY
- **Mobile**: when `window.innerWidth < 900`, the Topbar shows a hamburger button and the sidebar turns into a drawer
- **provide/inject**: the three functions `navigate` / `openDocDetail` / `backToDocs` are provided to descendant views

### 4.2 OverviewPage (Overview, new)

- **Data source**: `client.listDocumentFiles(1, 100)` + `client.listDocumentParses(1, 100)` fetched concurrently
- **4 stat cards** (`StatCard.vue`): total documents, parsing, parsed, parse failed; numbers count up over 0.9s cubic ease-out
- **Parse-status donut** (`ChartDonut.vue`): 4 segments (parsed/unparsed/parsing/failed), stroke-dasharray drawing animation 0.9s, staggered 0.12s per segment; the center shows the total count
- **Parse-throughput line chart** (`ChartLine.vue`): parse tasks completed in the last 30 days (bucketed by parses.parsedAt), Catmull-Rom smoothed curve, stroke-dashoffset drawing animation 1.1s
- **Recent uploads feed**: the first 5 document records; clicking a row uses the injected `openDocDetail(f.id)` to enter the detail page

### 4.3 DocManagePage (Doc Management)

- **StatusStrip**: 4-state switch (All/Pending parse/Parsing/Failed); the active state has a primary border + 3px ring; the selected state is passed to `client.listDocumentFiles` as the `parseStatus` parameter, triggering backend filtering
- **upload-zone**: click + drag-and-drop upload, progress bar `progress-track` + `progress-fill`
- **table.data-table**: custom table (file name/type/upload time/parse status/actions); rows are clickable to enter the detail
- **Polling**: the parsing status polls automatically (5s interval, stops when there are no active tasks)
- **Pagination**: EP `el-pagination`
- **Business logic preserved**: `client.listDocumentFiles / uploadDocumentFile / parseDocumentFile / deleteDocumentFile`; delete confirmation via `ElMessageBox.confirm`
- **Directory structure**: logic extracted into `views/DocManagePage/useDocManage.ts`; subcomponents are `FileUploadZone.vue` / `FileListTable.vue`

### 4.4 DocDetailPage (Document Detail, new)

- **Data source**: `client.getDocumentFile(id)` (returns `DocumentFileDetail`, including the parses array)
- **Route parameter**: `docId` prop, injected by `App.vue`
- **Page structure**: back button + file info (file name/type/upload time) + 4 mini stat cards (parsed/parsing/failed/VLM/Pipeline counts) + parses list
- **Each parse card**:
  - Top: model badge (VLM=amber / Pipeline=green) + status badge + parse time + delete button
  - Error message: error-box (red background)
  - Action button group (parsed state): Markdown / HTML preview / JSON / V2 / Model / Layout / Images / Export MD / Export HTML
  - Parsing state: gradient progress bar + "auto-refresh in 5 seconds" hint
- **Business logic preserved**: preview/export/delete calls equivalent to ParseResultsPage, using `useToast` instead of `ElMessage`
- **Polling**: polls `getDocumentFile(id)` every 5s while there are pending/parsing states
- **Safe preview**: Markdown / JSON / Images previews all go through `utils/preview.ts`, opening a new window via `Blob` + `URL.createObjectURL` and avoiding `document.write`; new windows carry `noopener,noreferrer`
- **Directory structure**: logic extracted into `views/DocDetailPage/useDocumentDetail.ts`; subcomponents are `FileHeader.vue` / `FileInfoCard.vue` / `ParseStatsCard.vue` / `ParseRecordCard.vue`

### 4.5 ParseResultsPage (Parse Results)

- **StatusStrip**: 4-state switch (All/Parsed/Parsing/Failed); the selected state is passed to `client.listDocumentParses` as the `status` parameter, triggering backend filtering
- **table.data-table**: file name/model/status/parse time/error message/actions columns
- **Button visibility logic**: when status=parsed all preview/export buttons are shown; the delete button is always shown
- **VLM and Pipeline modes uniformly show all buttons** (no longer filtered by modelVersion)
- **Business logic preserved**: `client.listDocumentParses / deleteDocumentParse / exportParseMarkdown / exportParseHtml`, plus `client.getDocumentFile(fileId)` to fetch the detail and then access `parse.{markdownContent|contentList|contentListV2|modelJson|layoutJson|images}`
- **Pagination**: EP `el-pagination`
- **Safe preview**: Markdown / JSON / Images previews all go through `utils/preview.ts`, opening a new window via `Blob` + `URL.createObjectURL` and avoiding `document.write`; new windows carry `noopener,noreferrer`
- **Directory structure**: logic extracted into `views/ParseResultsPage/useParseResults.ts`; subcomponent is `ParseResultsTable.vue`

### 4.6 SearchPage (Search Test)

- **Search bar**: keyword input + phrase-query checkbox + search button + advanced-filter button (with active dot) + clear button + Dev mode toggle
- **Result list** (card style, replacing el-table):
  - Card header: page-number badge + block-type badge + subType + match-type badge + textFormat badge + sequence number
  - Card body: document title + matched text (keywords highlighted with `<mark>`) + caption preview
  - Card footer: BM25 score bar + mineruScore score bar + textLevel + bbox + creation time + "view blockData" expand button
- **Expanded detail**: 5 detail-cells for BM25 / mineruScore / bbox / textLevel / Segment ID + in Dev mode a dev-block (raw blockData JSON, dark background)
- **[Gen-2] Advanced filter drawer** (AppDrawer):
  - blockType (el-select filterable + allow-create; see the original spec for candidates)
  - blockSubType / pageNumber / textLevel / textFormat / parseId / documentFileId / hasImage
  - All 8 minerU filter parameters are preserved; empty values are not passed through (zero regression)
- **Business logic preserved**: `client.searchTest(query, phrase, 20, undefined, ...8 minerU parameters)`, with the same call order as the original ParseResultsPage
- **Keyword highlighting**: `highlightText(text, query)` wraps hits in `<mark>`; matched text is rendered via `v-html` and safely escaped with `escapeHtml`
- **Directory structure**: logic extracted into `views/SearchPage/useSearch.ts` + `searchFormatters.ts`; subcomponents are `SearchBar.vue` / `SearchFiltersDrawer.vue` / `SearchResultCard.vue`

## 5. Style Standards

### 5.1 Global Style Hierarchy

Import order in `main.ts` (later imports override earlier ones):

1. `./styles/element-variables.scss` — EP theme SCSS (`@forward` + `@use` injecting the primary color `#4F46E5`)
2. `./styles/tokens.scss` — `:root` CSS variables (copied verbatim from the mockup, mirroring the EP variables)
3. `./styles/app.scss` — `@use` aggregation entry, split by responsibility into SCSS partials (`base/` / `layout/` / `components/` / `pages/` / `utilities/`)
4. `./style.css` — typography helper classes (`.page-header` / `.page-title` / `.page-subtitle`)

`app.scss` no longer contains a global element reset or ID selectors:
- the reset is confined to the `.doctheca-admin *` scope
- `#app` / `#view` / `#toast-root` became `.app` / `.view` / `.toast-root`
- element selectors were migrated to the corresponding classes to avoid polluting Element Plus default styles

### 5.2 Design Tokens (tokens.scss)

| Token category | Key variables | Source |
|------|------|------|
| Primary color | `--primary: #4f46e5` / `--primary-hover: #4338ca` / `--primary-soft: #eef2ff` / `--primary-line: #c7d2fe` | Mockup |
| Text colors | `--ink: #0f172a` / `--text: #1f2937` / `--text-2: #475569` / `--text-3: #94a3b8` | Mockup |
| Background/surface | `--bg: #f8fafc` / `--surface: #ffffff` / `--surface-2: #f1f5f9` | Mockup |
| Border | `--border: #e2e8f0` / `--border-2: #eef2f7` | Mockup |
| Shadow | `--shadow-sm` / `--shadow` / `--shadow-hover` / `--shadow-float` | Mockup |
| Motion curves | `--ease: cubic-bezier(0.4, 0, 0.2, 1)` / `--spring: cubic-bezier(0.34, 1.56, 0.64, 1)` | Mockup |
| Font | `--mono: 'SF Mono', Menlo, Monaco, Consolas, monospace` | Mockup |
| EP mirror | `--el-color-primary: var(--primary)` etc. | Synced with the primary color |

### 5.3 Component Mapping

| Component category | Implementation |
|------|------|
| Buttons (`.btn` / `.btn-ghost` / `.btn-danger` / `.btn-sm`) | Hand-written SCSS classes, driven by design tokens |
| Cards (`.card` / `.hoverable` / `.card-head` / `.card-title` / `.card-sub`) | Hand-written SCSS classes |
| Tables (`table.data-table` / `.table-wrap`) | Hand-written native `<table>` + SCSS |
| Badges (`.badge.gray/green/amber/red/blue/indigo` + `.dot` + `.pulse`) | Hand-written SCSS classes |
| Chips / Strip / Stat / Feed | Hand-written SCSS classes (used by components such as `StatusStrip.vue` / `StatCard.vue`) |
| Drawer (`AppDrawer.vue` + `.drawer` + `.overlay`) | Teleport + hand-written SCSS, spring entrance 0.3s |
| Modal (`AppModal.vue` + `.modal` + `.overlay`) | Teleport + hand-written SCSS, scale + translateY spring entrance |
| Toast (`AppToast.vue` + `.toast` + `useToast`) | Teleport + global singleton, success/error states, spring entrance 0.3s |
| Charts (`ChartLine.vue` / `ChartDonut.vue`) | Native SVG, stroke-dashoffset/dasharray drawing animation; `ChartLine`'s `labels` are HTML/SVG-escaped before being spliced into the SVG |
| Icons (`Icon` / `iconHtml(name)`) | `utils/icons.ts`, 27 linear SVGs, stroke 1.6 round |
| Input / Select / number input / Switch / Checkbox | EP components (`el-input` / `el-select` / `el-input-number` / `el-switch` / `el-checkbox`) |
| Pagination | EP `el-pagination` (layout="sizes, prev, pager, next, jumper") |
| Confirm dialog | EP `ElMessageBox.confirm` (kept, equivalent to window.confirm) |
| Loading | EP `v-loading` directive |
| Error/success notifications | `useToast()` composable, replacing `ElMessage` |

> Status mapping: `getFileStatusLabel(status)` returns Chinese labels; `statusBadgeHtml(status)` returns a badge HTML string.

### 5.4 Cleaned-Up Dead Code

The following CSS classes have been removed from style.css (the corresponding features were never implemented or were deleted):
- Segment Refinement related (`.segment-*`, `.split-*`, `.profile-*`)
- Legacy dialogs (`.dialog-*`, `.modal-*`)
- Unused filter bar (`.filter-bar*`, `.filter-input`)
- 2026-07-20 redesign: switched from ADR-001 EP component wrappers to mockup-aligned hand-written SCSS classes (EP kept only for forms/pagination)

## 6. API Client

Admin business endpoints and parameters stay unchanged; all modules reuse the shared Axios client with cookie session and 401/403 handling.

The client is split into multiple modules by domain; `services/docApi.ts` continues to act as a compatibility facade exporting `createDocApiClient` and all types:

| File | Responsibility |
|------|------|
| `services/types.ts` | Shared interface types (`SearchResult` / `DocumentFile` / `DocumentFileDetail` / `DocumentParse` / `ApiResponse` / `DocPagedResponse`) |
| `services/httpClient.ts` | Shared Axios instance, single-flight refresh, 401 retry and 403 notification |
| `services/authApi.ts` | login / refresh / logout / session; session state contains no tokens |
| `services/documentApi.ts` | Document files: upload, list, detail, parse, delete, export Markdown/HTML |
| `services/parseApi.ts` | Parse records: list, delete, export Markdown/HTML |
| `services/searchApi.ts` | Search test: searchTest |
| `services/exportApi.ts` | Export downloads: Markdown / HTML blob download helpers |
| `services/error.ts` | `getDocErrorMessage` |
| `services/docApi.ts` | Compatibility facade: delegates internally to the modules, keeping the original export signatures |

| Method | Endpoint | Description |
|------|------|------|
| `login` | `POST /admin/auth/login` | Identity admin account/password login |
| `getSession` | `GET /admin/auth/session` | Get the current admin session |
| `refreshSession` | `POST /admin/auth/refresh` | Rotate the session using the HttpOnly refresh cookie |
| `logout` | `POST /admin/auth/logout` | Revoke the refresh token and clear cookies |
| `uploadDocumentFile` | `POST /admin/document-files/upload` | Upload a file (multipart) |
| `listDocumentFiles` | `GET /admin/document-files` | File list (pagination + parseStatus + fileName filters) |
| `getDocumentFile` | `GET /admin/document-files/:id` | File detail (including parses + images) |
| `parseDocumentFile` | `POST /admin/document-files/:id/parse` | Trigger parsing (modelVersion parameter) |
| `deleteDocumentFile` | `DELETE /admin/document-files/:id` | Delete a file |
| `exportMarkdown` / `exportHtml` | `GET /admin/document-files/:id/export/*` | Export |
| `listDocumentParses` | `GET /admin/document-parses` | Parse record list (pagination + status + search filters) |
| `deleteDocumentParse` | `DELETE /admin/document-parses/:id` | Delete a parse record |
| `exportParseMarkdown` / `exportParseHtml` | `GET /admin/document-parses/:id/export/*` | Export parse results |
| `searchTest` | `GET /admin/documents/search` | Search test (passes through the 8 minerU filter parameters) |

## 7. Testing

The frontend has **no unit-test framework** (package.json configures neither vitest nor jest). Verification approach:
1. `npm run build` (vue-tsc type check + vite build) must pass with zero errors and zero warnings
2. Manual verification of anonymous loading, wrong password, regular-account rejection, admin login, automatic cookie refresh, 401/403 handling and logout

Backend unit tests are unaffected by the frontend refactor (the frontend refactor does not change API contracts).

## 8. Refactoring Log

### 2026-07-29: Identity Admin Authentication Design

- The admin console restored application-layer authentication; only accounts whose Identity JWT contains `role=admin` can use it.
- Credentials changed to Access/Refresh HttpOnly Cookies; the frontend holds no tokens.
- Added a login page, startup session check, single-flight refresh, logout and 401/403 handling.
- Static files and the SPA fallback remain anonymous; all admin business APIs require the admin policy.
- QuestionBank (now the external repository Quaestura, ADR-0011) endpoints moved out of `/admin` into a separate read-only internal API that does not reuse the browser admin session (that internal API was later removed entirely).

### 2026-07-22: Frontend Large-File Refactor & Bug Fixes

- **Input**: audit findings from `docs/development/frontend-audit-2026-07.md` + task plan task-10-15
- **Bug fixes**:
  - StatusStrip did not actually filter the list in `DocManagePage` / `ParseResultsPage` — the `parseStatus`/`status` parameters are now passed
  - `document.write` new-window previews posed an XSS risk — uniformly migrated to `utils/preview.ts` using `Blob` + `URL.createObjectURL`, with new windows carrying `noopener,noreferrer`
  - `ChartLine.vue`'s `labels` were spliced into the SVG unescaped — HTML/SVG escaping added
- **Large-file splits**:
  - `styles/app.scss` (1668 lines) split into SCSS partials: base/reset, layout/shell, components, pages, utilities
  - `SearchPage.vue` (610 lines) split into the `SearchPage/` directory (SearchPage.vue / SearchBar.vue / SearchFiltersDrawer.vue / SearchResultCard.vue / useSearch.ts / searchFormatters.ts)
  - `DocDetailPage.vue` (571 lines) split into the `DocDetailPage/` directory (DocDetailPage.vue / FileHeader.vue / FileInfoCard.vue / ParseStatsCard.vue / ParseRecordCard.vue / useDocumentDetail.ts)
  - `ParseResultsPage.vue` (448 lines) split into the `ParseResultsPage/` directory (ParseResultsPage.vue / ParseResultsTable.vue / useParseResults.ts)
  - `DocManagePage.vue` (365 lines) split into the `DocManagePage/` directory (DocManagePage.vue / FileUploadZone.vue / FileListTable.vue / useDocManage.ts)
  - `services/docApi.ts` (252 lines) split by domain into `types.ts` / `documentApi.ts` / `parseApi.ts` / `searchApi.ts` / `exportApi.ts` / `error.ts`; `docApi.ts` kept as a compatibility facade
- **Style-pollution remediation**:
  - `* { ... }` reset confined to `.doctheca-admin *`
  - `#app` / `#view` / `#toast-root` changed to `.app` / `.view` / `.toast-root`
  - `App.vue` / `index.html` / `AppToast.vue` updated accordingly
- **Zero business-logic changes**: API endpoints/parameters/validation/polling/confirm dialogs fully equivalent

### 2026-07-20: High-Fidelity Mockup Redesign

- **Input**: `prototype/admin-console-redesign.html` (mockup, Doctheca portion)
- **Output**: 5 views (OverviewPage new / DocManagePage rewritten / DocDetailPage new / ParseResultsPage rewritten / SearchPage rewritten) + 8 components (Sidebar / Topbar / AppDrawer / AppModal / AppToast / ChartLine / ChartDonut / StatCard / StatusStrip) + 3 composables (useToast / useCountUp) + 1 icons utility
- **Styles**: `tokens.scss` (CSS variables) + `app.scss` (component class library) + `element-variables.scss` (EP primary color `#4F46E5`) + `style.css` (typography helpers)
- **Routing**: hash routing (4 main pages + detail subpage), provide/inject cross-level communication
- **Zero business-logic changes**: `git diff` is purely presentation-layer; API endpoints/parameters/validation/polling/confirm dialogs fully equivalent (self-check: see `Redesign-2026-07-Admin-Console.md` §zero-business-change self-check checklist)
- **Design document**: `docs/overview/Redesign-2026-07-Admin-Console.md` (authoritative spec, including design tokens / component patterns / file inventory / zero-business-change self-check)
- **EP retention scope**: el-input / el-select / el-input-number / el-pagination / el-switch / el-checkbox / ElMessageBox (form and pagination controls); all other presentation-layer components (button / card / table / drawer / modal / toast / sidebar / topbar / charts) are hand-written for high fidelity

### 2026-07-04: Removed Identity Authentication

- Deleted `LoginPage.vue` and `authService.ts` (no login page, no token storage)
- `App.vue` removed the login check, user area and logout button
- `docApi.ts` switched to plain axios; `createAuthenticatedClient` removed
- Backend removed `AuthEndpoints.cs`, the JWT Bearer middleware and the IdentityService HttpClient
- `.gitignore` removed the `authService.js` entry

### 2026-07-10: ADR-001 Migration to Element Plus

- Reintroduced element-plus ^2.10.0 / @element-plus/icons-vue ^2.3.1 / sass ^1.80.0 / unplugin-auto-import ^0.18.3 / unplugin-vue-components ^0.27.4
- vite.config.ts enabled AutoImport + Components (ElementPlusResolver, importStyle: false)
- Added `src/styles/element-variables.scss` (@forward + @use injecting brand SCSS tokens)
- main.ts imports element-variables.scss to compile the full themed CSS
- Retained minerU Gen-2 retrieval capabilities (DS-13~DS-18): 8 minerU filter parameters + row-expanded blockData detail + standalone mineruScore display

### 2026-07-03: UI Refactor

- Removed the element-plus / @element-plus/icons-vue / marked / @types/marked dependencies
- Cleaned about 600 lines of dead code from style.css and unified CSS variable naming
- (Note: this refactor was later partially rolled back by ADR-001, then further evolved by the 2026-07-20 redesign)
