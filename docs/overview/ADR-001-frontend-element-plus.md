# ADR-001: Adopt Element Plus in the Doctheca Admin Frontend and Replace Hand-Written CSS

> **Migration note (2026-09-24)**: The `src/admin_portal` / `admin_portal` paths in this document were migrated out with the Admin Portal into the external repository [Ruoyu.Admin](https://github.com/philfanzhou/Ruoyu.Admin) (ADR-0010, extract ruoyu-admin), and no longer exist in this repository. This document is kept in its original form as a historical record and is not to be used as a current runbook.


## Status

Accepted — 2026-07-10

## Context

The Doctheca admin frontend initially used a 914-line hand-written `style.css` plus hand-written component classes across three pages (`.btn` / `.card` / `.data-table`, etc.), depending only on `vue + axios`. That decision (recorded in `frontend/docs/frontend-spec.md` §2) was reasonable at the time — minimal components, it worked.

After the minerU second-generation retrieval capability went live (SearchPage.vue advanced filtering + expansion + responsive multi-column), we confirmed that the pure hand-written approach **does not reach "truly production-grade"** in the following dimensions:

1. **Interaction polish**: micro-animations such as loading skeletons, row hover transitions, form validation linkage, Drawers, and empty-state illustrations require large amounts of hand-written JS + CSS transitions, and every page copy **introduces subtle inconsistencies** (the main source of the "half-finished demo feel").
2. **Cross-device consistency**: three-tier responsive layouts for phone + PC + tablet require re-laying out the hand-written grid on every page, with no guarantee of consistent spacing, font sizes, or breakpoints.
3. **Defect cost**: every new component requires covering the six stable states hover / focus / active / disabled / loading / empty; missing stable states in hand-written component classes is a breeding ground for bugs.
4. **Project trend**: admin_portal and teacher_portel already use Element Plus; doctheca is a high-frequency entry point for portal administrators, and **if its visuals/interactions can be unified with the existing portals**, user cognitive cost will drop.

## Decision

**Adopt Element Plus as the only UI component library of the doctheca admin frontend, migrate the three pages (DocManagePage / ParseResultsPage / SearchPage) plus the App.vue layout shell, and convert the existing CSS variable theme system into Element Plus SCSS design token sources.**

### Motivation Priorities

| Priority | Goal |
|--------|------|
| P0 | Interaction polish (refined transitions, form linkage, complete stable states) |
| P1 | PC + mobile + tablet three-tier responsiveness |
| P2 | Cross-project portal visual unification |

### Scope (migrate all at once, leave no half-set)

- ✅ All three pages + App.vue layout shell (sidebar / topbar / breadcrumb / responsive drawer)
- ✅ Base components fully replaced with EP: Button / Input / Select / Checkbox / Switch / Table / Drawer / Pagination / Tag / Skeleton / Empty / Message / MessageBox / Popconfirm
- ✅ The existing 914-line `style.css` distilled into an EP SCSS design token mapping (see §5); the old `style.css` split into small override files
- ❌ No changes to admin_portal / teacher_portal (unification to be assessed separately later; not covered by this ADR)
- ❌ No changes to the backend (API contracts / minerU retrieval / database: zero changes)

### Tech Stack Change

Before migration:
```
Vue 3.5 + TypeScript + Vite + pure hand-written CSS (style.css, 914 lines) + axios
```

After migration:
```
Vue 3.5 + TypeScript + Vite + Element Plus (on-demand import, unplugin-auto-import or manual)
  + EP SCSS design token overrides (from existing style.css variables)
  + axios (retained)
```

## Selection Rationale

### Why Not Naive UI / Arco Design / Ant Design Vue

| Option | Assessment |
|------|------|
| **Element Plus** ✅ selected | Mature and stable, most validated in admin scenarios; native to Vue 3.5; good on-demand import support; already used by admin_portal/teacher_portal = immediate unification |
| Naive UI | TS-native, small bundle, more modern design; but fewer production pitfalls encountered, slower issue response than EP, and higher cost to manually migrate existing portals |
| Arco Design | Admin-specialized, refined components; bytes style differs from the project's existing one; smaller community than EP |
| Ant Design Vue | Most mature admin library; heavy bundle, saturated default style; higher cost to migrate existing portals |
| Pure hand-written (status quo) | Current path — interaction polish + three-device consistency + stable states = ongoing effort continuously exceeds the one-time cost of completing EP; unacceptable |

### Stability First > Aesthetic Novelty

The real meaning of "polished" in this project is: **smooth transitions, complete states, cross-device consistency, few bugs** — Element Plus beats the younger Naive/Arco on all four. Aesthetics can be adjusted separately via token overrides.

## Theme Strategy (CSS Variables → EP SCSS Mapping)

The CSS variables in the existing `style.css :root` continue to be the single source of truth for **brand colors / spacing / border radius / transitions**; two sets of tokens are no longer maintained.

### Variable Mapping Table (CSS Variables → EP SCSS Variables)

| Existing CSS variable | EP SCSS variable | Notes |
|-------------|-------------|------|
| `--primary-color: #2563eb` | `$--color-primary` / `$--color-primary-light-3~9` series | Primary color ladder auto-generated from `#2563eb` |
| `--primary-dark: #1d4ed8` | `$--color-primary-dark-2` | Hover color |
| `--success-color: #10b981` | `$--color-success` | |
| `--danger-color: #ef4444` | `$--color-danger` | |
| `--warning-color: #f59e0b` | `$--color-warning` | |
| `--info-color: #3b82f6` | `$--color-info` | |
| `--text-primary: #111827` | `$--color-text-primary` | |
| `--text-secondary: #6b7280` | `$--color-text-regular` | |
| `--text-muted: #9ca3af` | `$--color-text-secondary` | |
| `--border-color: #e5e7eb` | `$--border-color-base` | |
| `--border-light: #f3f4f6` | `$--border-color-light` | |
| `--card-bg: #fff` | `$--bg-color` | |
| `--bg-color: #f5f7fa` | `$--bg-color-page` | |
| `--shadow-sm/md` | `$--box-shadow-light` / `$--box-shadow` | |
| `--radius-sm/md/lg` | `$--border-radius-small/medium/large` (needs customization) | EP defaults are smaller; keep 4/6/8 |
| `--transition-fast: 150ms ease` | Custom: override transition-duration (EP defaults to 0.3s, needs tightening) | |
| `--transition-base: 200ms ease` | | |

### SCSS Token Override File (`src/styles/element-variables.scss`)

```scss
// Inject brand values before the EP default variables
@forward 'element-plus/theme-chalk/src/common/var.scss' with (
  $colors: (
    'primary': ('base': #2563eb),
    'success': ('base': #10b981),
    'warning': ('base': #f59e0b),
    'danger':  ('base': #ef4444),
    'error':   ('base': #ef4444),
    'info':    ('base': #3b82f6),
  ),
  $text-color: (
    'primary':   #111827,
    'regular':   #6b7280,
    'secondary': #9ca3af,
    'placeholder': #c0c4cc,
  ),
  $border-color: (
    'base':   #e5e7eb,
    'light':  #f3f4f6,
    'lighter': #f3f4f6,
  ),
  $bg-color: (
    '':        #f5f7fa,
    'page':    #f5f7fa,
    'overlay': #ffffff,
  ),
  $border-radius: (
    'small':  4px,
    'medium': 6px,
    'large':  8px,
  ),
  $box-shadow: (
    'light':  0 1px 2px 0 rgba(0,0,0,0.05),
    '':       (0 4px 6px -1px rgba(0,0,0,0.1), 0 2px 4px -1px rgba(0,0,0,0.06)),
  ),
  $transition-duration: (
    '':     0.2s,
    'fast': 0.15s,
  ),
);

// Explicitly import EP base styles
@use 'element-plus/theme-chalk/src/index.scss' as *;
```

### Tightening Transitions (EP Default 0.3s → Existing 0.15/0.2s)

EP default animations are relatively long (0.3s). As required — "polished but not sluggish" — override the global transition variables:

```scss
:root {
  --el-transition-duration: 0.2s;
  --el-transition-duration-fast: 0.15s;
}
```

### Keep `@use` Instead of `@import`

EP v2.7+ recommends `@use`, avoiding the future-deprecation warning of `@import` in Vite.

## Migration Implementation Points

### Migrate by Layer (Pages → Layout → Fine Tuning)

1. **Element-level replacement**: `btn` / `card` / `input-wrap` / `data-table` / `tag` / `empty-state` → EP components
2. **Table replacement**: `.data-table` → `el-table` + `el-pagination`; zebra / hover / sticky header / column selector carried over
3. **Form replacement**: all filter-area inputs → `el-input` / `el-select` / `el-checkbox` / `el-date-picker`; validation linkage via EP `el-form`
4. **Layout replacement**: App.vue's hand-written sidebar CSS → `el-menu` (collapse + responsive drawer); topbar breadcrumb → `el-breadcrumb`; empty states → `el-empty`; text toasts → `el-message` / `el-message-box`
5. **Responsive takeover**: the PC/tablet/phone three tiers handled by `el-row` + `el-col` breakpoints (`sm / md / lg / xl`) plus sidebar `v-if="!isMobile"` linkage; no more hand-written media queries

### "Polish" Details Not to Be Missed

- **Skeleton screens**: use `el-skeleton` for table loading, replacing the "Searching..." text
- **Empty unification**: all empty states use `el-empty` (unified description copy)
- **Message / MessageBox unification**: success/failure feedback uses `el-message`; destructive operations use `el-message-box` (`confirmDelete` is currently `window.confirm`)
- **Drawer**: advanced filter drawer uses `el-drawer` (SearchPage currently uses inline collapse → change to Drawer)
- **Table row hover / stripe**: controlled via `el-table` `stripe` + `row-class-name`
- **Pagination**: `el-pagination` (layout includes `sizes` / `prev` / `pager` / `next` / `jumper` / `total`)

### Checklist for Preserving the Existing Brand

- [ ] Primary color #2563eb (EP `$--color-primary` injected correctly)
- [ ] Shadows `--shadow-sm` / `--shadow-md` (EP `$--box-shadow` mapping)
- [ ] Border radius 4/6/8px (EP `$--border-radius` customized)
- [ ] Font size ladder (EP `$--font-size-*` keeps defaults, no separate scale)
- [ ] Transition durations tightened to 0.15/0.2s (EP `$--transition-duration` override)
- [ ] Font stack: keep `-apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC'...` (only appended in `:root`, untouched)

## Consequences

### Positive
- Stable across three device tiers (EP built-in responsive breakpoints + collapsible menu)
- Complete stable states (hover / focus / active / disabled / loading / empty / error states implemented internally by EP, no longer hand-written)
- Unified across portals (shares class names, sizing, and spacing system with admin_portal/teacher_portal)
- Hand-written stylesheet reduced from 914 lines to pure token overrides + a few scoped overrides (estimated < 150 lines)

### Risks and Mitigations

| Risk | Mitigation |
|------|------|
| EP bundle grows → slower first paint | On-demand import via `unplugin-element-plus` / manual partial imports; first paint < 200KB gzip |
| Migrating the 914-line old style.css all at once easily misses states | Replace table by table: map the header first → then replace the table body → then add empty/loading states; run `npm run build` after each page is replaced |
| Theme color injection fails, falling back to EP default blue | After build, verify in the browser that the primary color === #2563eb; pre-check locally with `npm run dev` |
| Form validation linkage (docApi error code format) incompatible with EP `el-form` validation format | Explicitly map axios error codes to EP validation messages; do not hard-wire in the EP rules layer |
| During migration, new minerU retrieval fields (blockData/minerUscore/subType, etc.) require re-testing table columns | SearchPage.vue already has minerU columns; after EP migration they must be preserved and display correctly |

### Effort Estimate

- Base replacement (elements + tables + forms): 1 week
- Responsive + polished states (skeleton / drawer / message / transition): 3 days
- Theme token overrides + visual fine tuning: 2 days
- Regression testing of the three pages: 2 days

Total: about 2 weeks.

## References

- EP official theming docs: https://element-plus.org/zh-CN/guide/theming.html
- Full EP SCSS variables: https://github.com/element-plus/element-plus/blob/dev/packages/theme-chalk/src/common/var.scss
- Existing portal EP practice: `src/admin_portal/frontend` (Element Plus already integrated)
- This frontend's existing style source: `frontend/src/style.css` (CSS variables in the §5 mapping table)
- minerU second-generation retrieval design: `docs/modules/DocumentSearch/03-DESIGN.md §Evolution (DS-13~DS-18 merged to master)`

## Implementation Deviations

### Implementation Deviation 1: SCSS Injection Method Adjusted (additionalData → importStyle: false)

**Symptom**: after configuring `css.preprocessorOptions.scss.additionalData: '@use "@/styles/element-variables.scss" as *;'` in `vite.config.ts` per §5.2, `npm run build` reported `Error: [sass] Module loop: this module is already being loaded.`. The cause is that `additionalData` injects the `@use` into **every** SCSS file, including `element-variables.scss` itself, forming a circular load.

**Adjustment**:
1. Remove the `css.preprocessorOptions.scss.additionalData` block from `vite.config.ts`.
2. Add `importStyle: false` to both the `AutoImport` and `Components` `ElementPlusResolver` configurations, disabling per-component pre-built CSS auto-import.
3. The themed stylesheet is compiled once by `element-variables.scss` (`@forward` + `@use index.scss`); importing it once in `main.ts` applies the full themed CSS.

**Impact**: no functional loss. The themed CSS is still produced by SCSS compilation with brand values; only the injection path changed from "@use in every SCSS file" to "one-time import of the full compiled artifact in main.ts".

### Implementation Deviation 2: $box-shadow Multi-Shadow Values Need Parentheses

**Symptom**: in the §5.2 template, the `''` key of `$box-shadow` contained two comma-separated shadow values `0 4px 6px -1px rgba(0,0,0,0.1), 0 2px 4px -1px rgba(0,0,0,0.06),`; the SCSS parser misread the comma as a map entry separator and reported `expected ":"`.

**Adjustment**: wrap the multi-shadow value in parentheses `(0 4px 6px -1px rgba(0,0,0,0.1), 0 2px 4px -1px rgba(0,0,0,0.06))`. The §5.2 template in this ADR has been corrected accordingly.
