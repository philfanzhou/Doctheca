# DocLibrary 前端规范与安全审计报告

- **审计范围**: `src/services/ruoyu.doclibrary/frontend/src`
- **扫描文件**: 25 个（`.vue` / `.ts` / `.scss`，已排除 `node_modules/`）
- **审计提交**: 重点覆盖 `7b0dcd10`（前端重设计）
- **审计日期**: 2026-07-22
- **审计结论**: 不存在编译失败或运行崩溃类阻塞项；存在 1 处逻辑错误、2 处安全风险点、多处可维护性问题。

---

## 1. 执行摘要

| 维度 | 状态 | 说明 |
|------|------|------|
| TypeScript `any` | 无违规 | 未发现 `: any` / `as any`；仅有 `as Blob`、`as DocumentParse[]` 等安全类型断言 |
| XSS / `v-html` / `document.write` | 需关注 | `document.write` 用于新窗口预览（已转义），但方案本身存在 XSS 风险；`v-html` 全部用于硬编码 SVG/徽章 HTML，风险可控 |
| 重复代码 | 严重 | `escHtml/escAttr`、`downloadBlob`、`openJsonInNewWindow`、状态徽章、文件图标逻辑等在多页面重复 |
| 大文件 | 需拆分 | 4 个 `.vue` 视图 >=300 行，`app.scss` 1668 行 |
| 全局样式污染 | 需整改 | `app.scss` 使用 `*` reset 及大量元素/ID 选择器 |
| 未使用导入/变量 | 存在 | 重复 `inject` 导入、`toastSuccess` 未使用、`filterStatus` 未参与过滤 |
| API 调用一致性 | 良好 | 统一使用 `axios` + `docApi.ts` 封装，未混用 `fetch` |

---

## 2. 违规统计

| 优先级 | 数量 | 类型分布 |
|--------|------|----------|
| **P0** | 0 | -- |
| **P1** | 3 | 逻辑错误 1、安全风险 2 |
| **P2** | 5 | 重复代码 2、全局样式污染 1、大文件 1、XSS 潜在风险 1 |
| **NIT** | 5 | 重复导入、未使用变量、import 位置、风格不一致 |

**总计**: 13 项


## 3. 按优先级问题清单

### P1 -- 必须修复

#### P1-01: `ParseResultsPage.vue` / `DocManagePage.vue` 的 StatusStrip 未实际过滤列表

- **文件**: `src/views/ParseResultsPage.vue:19`, `src/views/DocManagePage.vue:25`
- **问题**: 两个页面都维护 `filterStatus` 响应式状态，点击 strip 会更新 `filterStatus`，但后续列表加载均未使用该状态。
  - `ParseResultsPage.vue` 的 `selectStrip` 仅赋值，未触发任何过滤或重新加载。
  - `DocManagePage.vue` 的 `selectStrip` 虽调用 `loadFileList()`，但 `loadFileList()` 中 `parseStatus` 参数固定传 `undefined`（第 33-36 行）。
- **影响**: 用户点击“已解析/解析中/失败”等状态条后，列表不会按状态过滤，与 `frontend-spec.md` 4.3/4.5 中“4 档状态切换”的交互预期不符。
- **建议**: 
  - 方案 A（推荐）：调用 `client.listDocumentParses` / `client.listDocumentFiles` 时传入 `status` 参数，后端如支持则直接过滤；若后端不支持，则在前端按 `filterStatus` 过滤当前页数据。
  - 方案 B：若仅为统计展示，移除 `filterStatus` 的 active 交互，改为纯展示条。

#### P1-02: `document.write` 用于新窗口预览，存在 XSS 风险

- **文件**: 
  - `src/views/DocDetailPage.vue:157, 198, 214`
  - `src/views/ParseResultsPage.vue:83, 186, 205`
- **问题**: 使用 `window.open('', '_blank')` + `document.write()` 在新窗口中写入 HTML。当前内容已用 `escHtml` / `escAttr` 转义，但 `document.write` 方案本身仍允许写入可执行内容；后续维护一旦遗漏转义即可导致 XSS。同时 `window.open('')` 与调用页同源，新窗口拥有 `window.opener`，存在 opener 劫持风险。
- **影响**: 中危。当前实现已转义，但属于“容易回退”的高风险模式。
- **建议**: 
  - 将预览内容写入 `Blob` + `URL.createObjectURL`（与 `openHtmlPreview` 做法一致），或创建沙箱 iframe；避免 `document.write`。
  - 如果必须保留，为新窗口设置 `noopener` 并在文档关闭前不再写入任何动态内容。

#### P1-03: `ChartLine.vue` 的 `labels` 未转义即插入 SVG

- **文件**: `src/components/ChartLine.vue:30-38`
- **问题**: `props.labels` 通过模板字符串直接拼接到 SVG `text` 元素中，再经 `v-html` 渲染。当前默认 labels 为硬编码日期字符串，但一旦父组件传入用户可控数据，将构成 XSS。
- **影响**: 当前调用点（`OverviewPage.vue`）为硬编码标签，风险低；但组件 API 存在安全隐患。
- **建议**: 对 `t` 进行 HTML/SVG 转义后再拼接，或将 labels 改为常规 `text` 元素渲染。


### P2 -- 建议修复

#### P2-01: 跨页面重复的工具函数与业务逻辑

- **文件**: 
  - `src/views/DocDetailPage.vue:124-257`
  - `src/views/ParseResultsPage.vue:71-282`
  - `src/views/DocManagePage.vue:167-191`
  - `src/views/OverviewPage.vue:62-84`
  - `src/composables/useToast.ts:36-48`
- **问题**: 以下函数在多个文件中重复实现，违反 DRY：
  | 函数 | 出现位置 |
  |------|----------|
  | `escHtml` | `DocDetailPage.vue:251`, `ParseResultsPage.vue:247` |
  | `escAttr` | `DocDetailPage.vue:255`, `ParseResultsPage.vue:251` |
  | `downloadBlob` | `DocDetailPage.vue:240`, `ParseResultsPage.vue:236` |
  | `openJsonInNewWindow` | `DocDetailPage.vue:184`, `ParseResultsPage.vue:172` |
  | `statusBadgeHtml` | `DocDetailPage.vue:140`, `ParseResultsPage.vue:272`, `DocManagePage.vue:183`, `OverviewPage.vue:78` |
  | `fileIconCls` / `fileExtLabel` | `DocDetailPage.vue:124`, `DocManagePage.vue:167`, `OverviewPage.vue:62` |
  | `escapeHtml` | `useToast.ts:36`（与 `escHtml` 功能重复但实现更完整） |
- **影响**: 维护成本高，一处修复需同步多文件；不同文件的 `escHtml` 实现不一致（一个转义 4 个字符，一个转义 5 个字符）。
- **建议**: 
  - 将通用函数收敛到 `src/utils/`（如 `html.ts`、`download.ts`、`badge.ts`、`file.ts`）。
  - 统一使用 `useToast.ts` 中更完整的 `escapeHtml`（含单引号），淘汰 `escHtml`。

#### P2-02: `app.scss` 存在全局样式污染

- **文件**: `src/styles/app.scss:5-67`
- **问题**: 
  - 使用 `* { margin: 0; padding: 0; box-sizing: border-box; }` 全局 reset。
  - 直接对 `html`、`body`、`button`、`input`、`select`、`textarea`、`svg` 等元素选择器设置样式。
  - 使用 `#app`、`#view`、`#toast-root` 等 ID 选择器。
- **影响**: 样式权重高、作用域不可控，容易覆盖 Element Plus 默认样式或未来引入的第三方组件；与 Vue 单文件组件的 scoped 风格不一致。
- **建议**: 
  - 将 reset 限定在应用根选择器（如 `.doclibrary-admin *`）或使用更轻量的 reset。
  - 元素级样式改为类名（如 `.docl-btn`、`.docl-input`）。
  - ID 选择器改为 class（`.view`、`.toast-root`）。

#### P2-03: 大文件清单

| 文件 | 行数 | 说明 |
|------|------|------|
| `src/styles/app.scss` | 1668 | 组件类库，可按组件拆分为多个 partial |
| `src/views/SearchPage.vue` | 610 | 检索页逻辑 + 模板 + 样式 |
| `src/views/DocDetailPage.vue` | 571 | 详情页逻辑 + 模板 + 样式，含重复预览函数 |
| `src/views/ParseResultsPage.vue` | 448 | 解析结果页，含重复预览函数 |
| `src/views/DocManagePage.vue` | 365 | 文档管理页 |

- **建议**: 
  - 将 `app.scss` 拆分为 `_buttons.scss`、`_cards.scss`、`_tables.scss` 等 partial，由 `app.scss` `@use` 聚合。
  - 将视图中的业务逻辑（预览/导出/删除）提取到 composables 或 utils，使视图文件控制在 300 行以内。

#### P2-04: `DocDetailPage.vue` 与 `ParseResultsPage.vue` 的预览/导出逻辑大面积重复

- **文件**: `src/views/DocDetailPage.vue:147-249`, `src/views/ParseResultsPage.vue:71-245`
- **问题**: 两个文件都实现了 `openMarkdown`、`openHtmlPreview`、`openJson`、`openImages`、`exportMarkdown`、`exportHtml`、`downloadBlob`、`escHtml`、`escAttr`、`openJsonInNewWindow` 等几乎相同的函数。
- **影响**: 任何导出/预览 bug 都需要在两地修复。
- **建议**: 抽象为 `useParsePreview(parseId)` / `useFilePreview(fileId)` composable 或 `previewService.ts`。


### NIT -- 可选优化

#### NIT-01: 重复导入 `inject`

- **文件**: 
  - `src/views/DocDetailPage.vue:2,8`
  - `src/views/DocManagePage.vue:2,9`
- **问题**: `inject` 已在顶部的 `vue` 导入中引入，又在文件中部单独 `import { inject } from 'vue'`。
- **建议**: 合并到顶部导入。

#### NIT-02: `App.vue` 中 `provide` 导入位置不当

- **文件**: `src/App.vue:138`
- **问题**: `import { provide } from 'vue'` 出现在 script setup 中间（第 138 行），不符合常见风格。
- **建议**: 移至第 2 行顶部导入语句中。

#### NIT-03: `App.vue` 中 `toastSuccess` 未使用

- **文件**: `src/App.vue:36`
- **问题**: 解构出 `toastSuccess` 但只使用了 `toastError`。
- **建议**: 移除未使用的 `toastSuccess`，或保留注释说明用途。

#### NIT-04: `ParseResultsPage.vue` 中 `filterStatus` 未参与过滤（已升至 P1-01）

- 见 P1-01。

#### NIT-05: `DocApiClient` 中类型断言混用

- **文件**: `src/services/docApi.ts:177,210,218,239`
- **问题**: 使用 `response.data as Blob`、`error as unknown as {...}`。虽类型安全，但可进一步通过 axios 泛型或自定义类型守卫消除断言。
- **建议**: 低优先级，可后续统一处理。


## 4. 详细文件清单

### 4.1 TypeScript 类型安全

| 文件 | 行号 | 问题 | 严重度 |
|------|------|------|--------|
| 全部 `.ts`/`.vue` | -- | 未发现 `: any` / `as any` | OK |
| `src/services/docApi.ts:103` | 103 | `Record<string, unknown>` 参数对象 | OK |
| `src/services/docApi.ts:177,210,218` | 177,210,218 | `response.data as Blob` | NIT |
| `src/services/docApi.ts:239` | 239 | `error as unknown as {...}` | NIT |

### 4.2 XSS / v-html / document.write

| 文件 | 行号 | 问题 | 严重度 |
|------|------|------|--------|
| `src/views/DocDetailPage.vue` | 157, 198, 214 | `document.write` 写入预览窗口 | P1 |
| `src/views/ParseResultsPage.vue` | 83, 186, 205 | `document.write` 写入预览窗口 | P1 |
| `src/components/ChartLine.vue` | 88, 91, 92, 30-38 | `v-html` 渲染未转义的 labels | P1 |
| `src/views/SearchPage.vue` | 280 | `v-html="highlightText(...)` 已 escapeHtml | OK |
| 多处 | 多处 | `v-html="iconHtml(...)"` 为硬编码 SVG | OK |
| 多处 | 多处 | `v-html="statusBadgeHtml(...)"` 为硬编码 HTML | OK |
| `src/components/AppToast.vue` | 28 | `v-html="t.html"`，message 已 escapeHtml | OK |

### 4.3 重复代码

| 函数 | 出现位置 | 严重度 |
|------|----------|--------|
| `escHtml` | `DocDetailPage.vue:251`, `ParseResultsPage.vue:247` | P2 |
| `escAttr` | `DocDetailPage.vue:255`, `ParseResultsPage.vue:251` | P2 |
| `downloadBlob` | `DocDetailPage.vue:240`, `ParseResultsPage.vue:236` | P2 |
| `openJsonInNewWindow` | `DocDetailPage.vue:184`, `ParseResultsPage.vue:172` | P2 |
| `statusBadgeHtml` | `DocDetailPage.vue:140`, `ParseResultsPage.vue:272`, `DocManagePage.vue:183`, `OverviewPage.vue:78` | P2 |
| `fileIconCls` / `fileExtLabel` | `DocDetailPage.vue:124`, `DocManagePage.vue:167`, `OverviewPage.vue:62` | P2 |
| `escapeHtml` vs `escHtml` | `useToast.ts:36`, 多个视图 | P2 |
| 预览/导出动作整体 | `DocDetailPage.vue:147-249`, `ParseResultsPage.vue:71-245` | P2 |

### 4.4 大文件

| 文件 | 行数 | 严重度 |
|------|------|--------|
| `src/styles/app.scss` | 1668 | P2 |
| `src/views/SearchPage.vue` | 610 | P2 |
| `src/views/DocDetailPage.vue` | 571 | P2 |
| `src/views/ParseResultsPage.vue` | 448 | P2 |
| `src/views/DocManagePage.vue` | 365 | P2（接近阈值，可暂不拆分） |

### 4.5 全局样式污染

| 文件 | 行号 | 问题 | 严重度 |
|------|------|------|--------|
| `src/styles/app.scss` | 5-9 | `* { ... }` 全局 reset | P2 |
| `src/styles/app.scss` | 11-14 | `html { ... }` 元素选择器 | P2 |
| `src/styles/app.scss` | 16-23 | `body { ... }` 元素选择器 | P2 |
| `src/styles/app.scss` | 45-50 | `button { ... }` 元素选择器 | P2 |
| `src/styles/app.scss` | 53-59 | `input, select, textarea { ... }` 元素选择器 | P2 |
| `src/styles/app.scss` | 61-63 | `svg { ... }` 元素选择器 | P2 |
| `src/styles/app.scss` | 65-67 | `#app { ... }` ID 选择器 | P2 |
| `src/styles/app.scss` | 313-326 | `#view { ... }` ID 选择器 | P2 |
| `src/styles/app.scss` | 1372-1380 | `#toast-root { ... }` ID 选择器 | P2 |

### 4.6 未使用导入/变量

| 文件 | 行号 | 问题 | 严重度 |
|------|------|------|--------|
| `src/views/DocDetailPage.vue` | 2, 8 | `inject` 重复导入 | NIT |
| `src/views/DocManagePage.vue` | 2, 9 | `inject` 重复导入 | NIT |
| `src/App.vue` | 138 | `provide` 导入位置不当 | NIT |
| `src/App.vue` | 36 | `toastSuccess` 未使用 | NIT |
| `src/views/ParseResultsPage.vue` | 19, 268-269 | `filterStatus` 已设置未用于过滤（见 P1-01） | P1 |
| `src/views/DocManagePage.vue` | 25, 161-165 | `filterStatus` 已设置但未传入 API | P1 |

### 4.7 API 调用模式

| 文件 | 说明 | 状态 |
|------|------|------|
| `src/services/docApi.ts` | 统一使用 `axios.create` 封装 | OK |
| 所有视图 | 统一调用 `createDocApiClient()` | OK |
| 无文件 | 未发现原生 `fetch` | OK |


## 5. 与文档一致性

### 5.1 一致点

- `frontend-spec.md` 明确允许使用 `document.write` + `escHtml` 进行新窗口预览（4.5），代码实现与其一致。
- `frontend-spec.md` 允许 `v-html` 渲染 `iconHtml` / `statusBadgeHtml` / `highlightText`（4.6、5.3），代码与其一致。
- `frontend-spec.md` 允许全局 `app.scss` 组件类库，代码与其一致。

### 5.2 潜在差异

- `frontend-spec.md` 4.3/4.5 描述 StatusStrip 为“4 档状态切换”，但未明确说明是否触发列表过滤。当前实现仅更新高亮状态，未实际过滤，存在歧义。
- `frontend-spec.md` 5.1 说明 `app.scss` 是“完整组件类库”，未对全局 reset/元素选择器/ID 选择器作出限制。当前实现符合字面描述，但不符合一般前端可维护性最佳实践。

### 5.3 建议同步的文档

- 若决定保留 `document.write`，建议在 `frontend-spec.md` 中显式记录该设计决策及安全约束（必须转义、`noopener` 等）。
- 若 StatusStrip 仅用于统计展示，建议更新 `frontend-spec.md` 描述，避免“状态切换”带来的过滤预期。


## 6. 修复优先级建议

| 优先级 | 建议修复项 | 预期收益 |
|--------|-----------|----------|
| **P0** | 无 | -- |
| **P1** | 1. 修复 StatusStrip 未实际过滤的问题（`ParseResultsPage.vue` / `DocManagePage.vue`）<br>2. 将 `document.write` 预览迁移为 Blob URL 或沙箱方案<br>3. 为 `ChartLine.vue` 的 labels 增加转义 | 消除逻辑错误与主要 XSS 风险 |
| **P2** | 1. 收敛重复工具函数到 `src/utils/`<br>2. 拆分 `app.scss`，减少全局样式污染<br>3. 拆分大文件（视图 <=300 行）<br>4. 统一使用 `escapeHtml` 替代分散的 `escHtml` | 提升可维护性、降低回归风险 |
| **NIT** | 1. 合并重复 `inject` 导入<br>2. 调整 `App.vue` 中 `provide` 导入位置<br>3. 移除未使用的 `toastSuccess` | 代码风格一致 |

## 7. 扫描命令与验证

本次审计使用以下命令：

```bash
# 文件列表与行数统计
find src -type f \( -name '*.vue' -o -name '*.ts' -o -name '*.scss' \) -not -path '*/node_modules/*' -exec wc -l {} + | sort -n

# TypeScript 类型检查
npx vue-tsc --noEmit

# 正则扫描（any / v-html / document.write / 全局样式等）
# 详见审查工具输出
```

`npx vue-tsc --noEmit` 在本次审计中**无输出**，即当前代码可通过类型检查。

---

*报告生成方式：只读扫描，未修改任何源文件。*
