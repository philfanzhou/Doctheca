# DocLibrary Admin 前端规格

## 1. 概述

DocLibrary Admin 是文档库的管理后台前端,供本地管理员上传文档、触发 MinerU 解析、查看解析结果、测试检索功能。

- 技术栈:Vue 3.5 + TypeScript + Vite + Element Plus(表单/分页控件)+ 手写 SCSS design-token 系统(展示层组件)
- 部署:由 DocLibrary 后端 Host 静态托管(`wwwroot/`),与后端同源,无需独立部署
- 访问:HTTP `:5012`

## 2. 依赖策略

**Element Plus 作为表单/分页 UI 库(按 ADR-001 引入),展示层组件(button / card / table / drawer / modal / toast / sidebar / topbar 等)按 2026-07-20 重设计稿手写实现。**

迁移令:ADR-001 + Redesign-2026-07-Admin-Console.md(`docs/overview/`)。

| 依赖 | 用途 | 必要性 |
|------|------|--------|
| `vue` | 框架(3.5) | 必需 |
| `typescript` | 类型 | 必需 |
| `vite` | 构建 | 必需 |
| `axios` | HTTP 请求 | 必需 |
| `element-plus` | 表单/分页控件(ElInput/ElSelect/ElInputNumber/ElPagination/ElSwitch/ElCheckbox/ElMessageBox) | 必需 |
| `element-plus/theme-chalk` | SCSS 主题(主色 token 镜像) | 必需 |
| `sass` | SCSS 编译 | 必需 |
| `unplugin-auto-import` / `unplugin-vue-components` | EP 组件按需自动导入 | 必需 |

**不在依赖内:**
- 其他 UI 库(Naive / Arco / Ant Design Vue / Vuetify)
- 图表库(SVG 折线/甜甜圈图由 `ChartLine.vue` / `ChartDonut.vue` 原生描画)
- `marked`(解析结果 Markdown 以 `<pre>` 原文展示)
- `@element-plus/icons-vue`(改用自维护 `utils/icons.ts`,27 个线性 SVG 图标)

## 3. 访问控制

DocLibrary 为**内网管理后台**,不实现应用层认证:

- 部署层网络隔离:仅内网可访问 `:5012` 端口与 `/admin/*` 路径
- 前端无登录页、无 Token 存储、无 axios 拦截器
- 后端无 JWT Bearer、无 Authentication / Authorization 中间件、无 Identity 集成
- 所有 `/admin/*` 端点 `AllowAnonymous`,直接接受请求

> **架构约束**:不再调用 QuantumZhou.Identity。原 AuthEndpoints / LoginPage / authService 已删除。

## 4. 页面结构

### 4.1 整体布局(App.vue)

```
┌────────────────────────────────────────────┐
│ Sidebar  │ Topbar(面包屑 + 时钟 + env-tag) │
│ 深色侧栏  ├────────────────────────────────┤
│  - Brand  │  #view(动态视图)                │
│  - 概览    │   - OverviewPage (#overview)    │
│  - 文档管理│   - DocManagePage (#docs)       │
│  - 解析结果│   - DocDetailPage (#detail/:id) │
│  - 检索测试│   - ParseResultsPage (#results) │
│  - Foot   │   - SearchPage (#search)        │
└────────────────────────────────────────────┘
```

- **路由**:Hash 路由(`#overview` / `#docs` / `#detail/:id` / `#results` / `#search`),由 `App.vue` 解析,不引入 vue-router
- **视图切换过渡**:`#view` 容器在切换时添加 `.leaving` class 触发 150ms 模糊+位移过渡,结束后 swap 内容
- **导航项**:概览、文档管理、解析结果、检索测试(4 项);`detail` 状态下侧栏高亮"文档管理"
- **侧栏指示器**:`Sidebar.vue` 内置 `.nav-indicator` 滑块,active 切换时 0.22s spring translateY 跟随
- **移动端**:`window.innerWidth < 900` 时,Topbar 显示 hamburger 按钮,侧栏转为抽屉式
- **provide/inject**:`navigate` / `openDocDetail` / `backToDocs` 三个函数 provide 给后代视图

### 4.2 OverviewPage(概览,新增)

- **数据来源**:`client.listDocumentFiles(1, 100)` + `client.listDocumentParses(1, 100)` 并发拉取
- **4 张统计卡**(`StatCard.vue`):文档总数、解析中、已解析、解析失败,数字滚动 0.9s cubic ease-out
- **解析状态甜甜圈**(`ChartDonut.vue`):4 段(已解析/未解析/解析中/失败),stroke-dasharray 描画动画 0.9s,逐段 0.12s 错峰;中心显示总数
- **解析吞吐折线**(`ChartLine.vue`):近 30 天完成的解析任务数(由 parses.parsedAt 分桶),Catmull-Rom 平滑曲线,stroke-dashoffset 描画动画 1.1s
- **最近上传 Feed**:前 5 条文档记录,点击行 inject 调用 `openDocDetail(f.id)` 进入详情页

### 4.3 DocManagePage(文档管理)

- **StatusStrip**:4 档状态切换(全部/待解析/解析中/失败),active 态 primary 边框 + 3px ring;选中状态会作为 `parseStatus` 参数传给 `client.listDocumentFiles`,触发后端过滤
- **upload-zone**:点击 + 拖拽上传,进度条 `progress-track` + `progress-fill`
- **table.data-table**:自定义表格(文件名/类型/上传时间/解析状态/操作),行可点击进入详情
- **轮询**:解析中状态自动轮询(5s 间隔,无活跃任务时停止)
- **分页**:EP `el-pagination`
- **业务逻辑保留**:`client.listDocumentFiles / uploadDocumentFile / parseDocumentFile / deleteDocumentFile`,删除确认 `ElMessageBox.confirm`
- **目录结构**:逻辑抽取到 `views/DocManagePage/useDocManage.ts`,子组件为 `FileUploadZone.vue` / `FileListTable.vue`

### 4.4 DocDetailPage(文档详情,新增)

- **数据来源**:`client.getDocumentFile(id)`(返回 `DocumentFileDetail`,含 parses 数组)
- **路由参数**:`docId` prop,由 `App.vue` 注入
- **页面结构**:返回按钮 + 文件信息(文件名/类型/上传时间)+ 4 张迷你统计卡(已解析/解析中/失败/VLM/Pipeline 计数)+ parses 列表
- **每条 parse 卡片**:
  - 顶部:模型 badge(VLM=amber / Pipeline=green)+ 状态 badge + 解析时间 + 删除按钮
  - 错误信息:error-box(红色背景)
  - 操作按钮组(parsed 状态):Markdown / HTML 预览 / JSON / V2 / Model / Layout / 图片 / 导出 MD / 导出 HTML
  - 解析中状态:渐变进度条 + "5 秒后自动刷新"提示
- **业务逻辑保留**:与 ParseResultsPage 等价的 preview/export/delete 调用,使用 `useToast` 替代 `ElMessage`
- **轮询**:有 pending/parsing 状态时 5s 轮询 `getDocumentFile(id)`
- **安全预览**:Markdown / JSON / Images 预览统一走 `utils/preview.ts`,通过 `Blob` + `URL.createObjectURL` 打开新窗口,避免 `document.write`;新窗口带 `noopener,noreferrer`
- **目录结构**:逻辑抽取到 `views/DocDetailPage/useDocumentDetail.ts`,子组件为 `FileHeader.vue` / `FileInfoCard.vue` / `ParseStatsCard.vue` / `ParseRecordCard.vue`

### 4.5 ParseResultsPage(解析结果)

- **StatusStrip**:4 档状态切换(全部/已解析/解析中/失败);选中状态会作为 `status` 参数传给 `client.listDocumentParses`,触发后端过滤
- **table.data-table**:文件名/模型/状态/解析时间/错误信息/操作列
- **按钮显示逻辑**:status=parsed 时显示所有预览/导出按钮;始终显示删除按钮
- **VLM 和 Pipeline 模式统一显示所有按钮**(不再按 modelVersion 过滤)
- **业务逻辑保留**:`client.listDocumentParses / deleteDocumentParse / exportParseMarkdown / exportParseHtml`,以及 `client.getDocumentFile(fileId)` 拉取详情后调用 `parse.{markdownContent|contentList|contentListV2|modelJson|layoutJson|images}`
- **分页**:EP `el-pagination`
- **安全预览**:Markdown / JSON / Images 预览统一走 `utils/preview.ts`,通过 `Blob` + `URL.createObjectURL` 打开新窗口,避免 `document.write`;新窗口带 `noopener,noreferrer`
- **目录结构**:逻辑抽取到 `views/ParseResultsPage/useParseResults.ts`,子组件为 `ParseResultsTable.vue`

### 4.6 SearchPage(检索测试)

- **检索栏**:关键词输入 + 短语查询 checkbox + 搜索按钮 + 高级筛选按钮(带 active dot)+ 清空按钮 + Dev 模式切换
- **结果列表**(卡片式,取代 el-table):
  - 卡片头:页码 badge + 块类型 badge + subType + 匹配类型 badge + textFormat badge + 序号
  - 卡片体:文档标题 + 匹配文本(关键词 `<mark>` 高亮)+ caption 预览
  - 卡片底:BM25 score bar + mineruScore score bar + textLevel + bbox + 创建时间 + "查看 blockData"展开按钮
- **展开详情**:BM25 / mineruScore / bbox / textLevel / Segment ID 5 张 detail-cell + Dev 模式下展示 dev-block(blockData 原始 JSON,深色背景)
- **[Gen-2] 高级筛选抽屉**(AppDrawer):
  - blockType(el-select filterable + allow-create,候选见原 spec)
  - blockSubType / pageNumber / textLevel / textFormat / parseId / documentFileId / hasImage
  - 8 个 minerU 过滤参数全部保留;空值不透传(零回归)
- **业务逻辑保留**:`client.searchTest(query, phrase, 20, undefined, ...8个minerU参数)`,调用顺序与原 ParseResultsPage 一致
- **检索关键词高亮**:`highlightText(text, query)` 用 `<mark>` 包裹命中词,匹配文本通过 `v-html` 渲染,使用 `escapeHtml` 安全转义
- **目录结构**:逻辑抽取到 `views/SearchPage/useSearch.ts` + `searchFormatters.ts`,子组件为 `SearchBar.vue` / `SearchFiltersDrawer.vue` / `SearchResultCard.vue`

## 5. 样式规范

### 5.1 全局样式层级

`main.ts` 引入顺序(后者覆盖前者):

1. `./styles/element-variables.scss` — EP 主题 SCSS(`@forward` + `@use` 注入主色 `#4F46E5`)
2. `./styles/tokens.scss` — `:root` CSS 变量(逐字搬运自样稿,与 EP 变量镜像)
3. `./styles/app.scss` — `@use` 聚合入口,按职责拆分为 SCSS partials(`base/` / `layout/` / `components/` / `pages/` / `utilities/`)
4. `./style.css` — 排版辅助类(`.page-header` / `.page-title` / `.page-subtitle`)

`app.scss` 不再包含全局元素 reset 或 ID 选择器:
- reset 限定在 `.doclibrary-admin *` 作用域内
- `#app` / `#view` / `#toast-root` 改为 `.app` / `.view` / `.toast-root`
- 元素选择器迁移到对应 class,避免污染 Element Plus 默认样式

### 5.2 Design Token(tokens.scss)

| Token 类别 | 关键变量 | 来源 |
|------|------|------|
| 主色 | `--primary: #4f46e5` / `--primary-hover: #4338ca` / `--primary-soft: #eef2ff` / `--primary-line: #c7d2fe` | 样稿 |
| 文字色 | `--ink: #0f172a` / `--text: #1f2937` / `--text-2: #475569` / `--text-3: #94a3b8` | 样稿 |
| 背景/表面 | `--bg: #f8fafc` / `--surface: #ffffff` / `--surface-2: #f1f5f9` | 样稿 |
| 边框 | `--border: #e2e8f0` / `--border-2: #eef2f7` | 样稿 |
| 阴影 | `--shadow-sm` / `--shadow` / `--shadow-hover` / `--shadow-float` | 样稿 |
| 动效曲线 | `--ease: cubic-bezier(0.4, 0, 0.2, 1)` / `--spring: cubic-bezier(0.34, 1.56, 0.64, 1)` | 样稿 |
| 字体 | `--mono: 'SF Mono', Menlo, Monaco, Consolas, monospace` | 样稿 |
| EP 镜像 | `--el-color-primary: var(--primary)` 等 | 同步主色 |

### 5.3 组件映射

| 组件类别 | 实现方式 |
|------|------|
| 按钮(`.btn` / `.btn-ghost` / `.btn-danger` / `.btn-sm`) | 手写 SCSS class,设计 token 驱动 |
| 卡片(`.card` / `.hoverable` / `.card-head` / `.card-title` / `.card-sub`) | 手写 SCSS class |
| 表格(`table.data-table` / `.table-wrap`) | 手写原生 `<table>` + SCSS |
| 徽章(`.badge.gray/green/amber/red/blue/indigo` + `.dot` + `.pulse`) | 手写 SCSS class |
| Chips / Strip / Stat / Feed | 手写 SCSS class(`StatusStrip.vue` / `StatCard.vue` 等组件使用) |
| 抽屉(`AppDrawer.vue` + `.drawer` + `.overlay`) | Teleport + 手写 SCSS,spring 入场 0.3s |
| 弹窗(`AppModal.vue` + `.modal` + `.overlay`) | Teleport + 手写 SCSS,scale + translateY spring 入场 |
| Toast(`AppToast.vue` + `.toast` + `useToast`) | Teleport + 全局单例,success/error 两态,spring 入场 0.3s |
| 图表(`ChartLine.vue` / `ChartDonut.vue`) | 原生 SVG,stroke-dashoffset/dasharray 描画动画;`ChartLine` 的 `labels` 在拼入 SVG 前经 HTML/SVG 转义 |
| 图标(`Icon` / `iconHtml(name)`) | `utils/icons.ts`,27 个线性 SVG,stroke 1.6 round |
| 输入框 / Select / 数字输入 / Switch / Checkbox | EP 组件(`el-input` / `el-select` / `el-input-number` / `el-switch` / `el-checkbox`) |
| 分页 | EP `el-pagination`(layout="sizes, prev, pager, next, jumper") |
| 确认对话框 | EP `ElMessageBox.confirm`(保留,与 window.confirm 等价) |
| Loading | EP `v-loading` 指令 |
| 错误/成功提示 | `useToast()` composable,替代 `ElMessage` |

> 状态映射:`getFileStatusLabel(status)` 返回中文标签;`statusBadgeHtml(status)` 返回 badge HTML 字符串。

### 5.4 已清理的死代码

以下 CSS 类已从 style.css 移除(对应功能未实现或已删除):
- Segment Refinement 相关(`.segment-*`、`.split-*`、`.profile-*`)
- 旧版弹窗(`.dialog-*`、`.modal-*`)
- 未使用的过滤器栏(`.filter-bar*`、`.filter-input`)
- 2026-07-20 重设计:从 ADR-001 EP 组件包装切换为样稿对齐的手写 SCSS class(EP 仅保留表单/分页)

## 6. API 客户端

> **业务逻辑零改动**:API 端点、请求/响应结构、参数顺序与 ADR-001 时保持一致。

客户端按领域拆分为多个模块,`services/docApi.ts` 继续作为兼容 facade 导出 `createDocApiClient` 与所有类型:

| 文件 | 职责 |
|------|------|
| `services/types.ts` | 共享接口类型(`SearchResult` / `DocumentFile` / `DocumentFileDetail` / `DocumentParse` / `ApiResponse` / `DocPagedResponse`) |
| `services/documentApi.ts` | 文档文件相关:上传、列表、详情、解析、删除、导出 Markdown/HTML |
| `services/parseApi.ts` | 解析记录相关:列表、删除、导出 Markdown/HTML |
| `services/searchApi.ts` | 检索测试:searchTest |
| `services/exportApi.ts` | 导出下载:Markdown / HTML blob 下载辅助 |
| `services/error.ts` | `getDocErrorMessage` |
| `services/docApi.ts` | 兼容 facade:内部委托到各模块,保留原有导出签名 |

| 方法 | 端点 | 说明 |
|------|------|------|
| `uploadDocumentFile` | `POST /admin/document-files/upload` | 上传文件(multipart) |
| `listDocumentFiles` | `GET /admin/document-files` | 文件列表(分页+parseStatus+fileName筛选) |
| `getDocumentFile` | `GET /admin/document-files/:id` | 文件详情(含 parses + images) |
| `parseDocumentFile` | `POST /admin/document-files/:id/parse` | 触发解析(modelVersion 参数) |
| `deleteDocumentFile` | `DELETE /admin/document-files/:id` | 删除文件 |
| `exportMarkdown` / `exportHtml` | `GET /admin/document-files/:id/export/*` | 导出 |
| `listDocumentParses` | `GET /admin/document-parses` | 解析记录列表(分页+status+search筛选) |
| `deleteDocumentParse` | `DELETE /admin/document-parses/:id` | 删除解析记录 |
| `exportParseMarkdown` / `exportParseHtml` | `GET /admin/document-parses/:id/export/*` | 导出解析结果 |
| `searchTest` | `GET /admin/documents/search` | 检索测试(8 个 minerU 过滤参数透传) |

## 7. 测试

前端**无单元测试框架**(package.json 未配置 vitest/jest)。验证方式:
1. `npm run build`(vue-tsc 类型检查 + vite 构建)必须通过,零错误零警告
2. 人工验证页面功能

后端 UT 不受前端重构影响(前端重构不改变 API 契约)。

## 8. 重构记录

### 2026-07-22: 前端大文件重构与问题修复

- **输入**:`docs/development/frontend-audit-2026-07.md` 审计结论 + 任务计划 task-10-15
- **问题修复**:
  - StatusStrip 在 `DocManagePage` / `ParseResultsPage` 中未实际过滤列表 — 已传入 `parseStatus`/`status` 参数
  - `document.write` 新窗口预览存在 XSS 风险 — 统一迁移到 `utils/preview.ts`,使用 `Blob` + `URL.createObjectURL`,新窗口带 `noopener,noreferrer`
  - `ChartLine.vue` 的 `labels` 未转义拼入 SVG — 已增加 HTML/SVG 转义
- **大文件拆分**:
  - `styles/app.scss`(1668 行)拆分为 SCSS partials:base/reset、layout/shell、components、pages、utilities
  - `SearchPage.vue`(610 行)拆分为 `SearchPage/` 目录(SearchPage.vue / SearchBar.vue / SearchFiltersDrawer.vue / SearchResultCard.vue / useSearch.ts / searchFormatters.ts)
  - `DocDetailPage.vue`(571 行)拆分为 `DocDetailPage/` 目录(DocDetailPage.vue / FileHeader.vue / FileInfoCard.vue / ParseStatsCard.vue / ParseRecordCard.vue / useDocumentDetail.ts)
  - `ParseResultsPage.vue`(448 行)拆分为 `ParseResultsPage/` 目录(ParseResultsPage.vue / ParseResultsTable.vue / useParseResults.ts)
  - `DocManagePage.vue`(365 行)拆分为 `DocManagePage/` 目录(DocManagePage.vue / FileUploadZone.vue / FileListTable.vue / useDocManage.ts)
  - `services/docApi.ts`(252 行)按领域拆分为 `types.ts` / `documentApi.ts` / `parseApi.ts` / `searchApi.ts` / `exportApi.ts` / `error.ts`,`docApi.ts` 保留为兼容 facade
- **样式污染整改**:
  - `* { ... }` reset 限定在 `.doclibrary-admin *`
  - `#app` / `#view` / `#toast-root` 改为 `.app` / `.view` / `.toast-root`
  - 同步更新 `App.vue` / `index.html` / `AppToast.vue`
- **业务逻辑零改动**:API 端点/参数/校验/轮询/确认框完全等价

### 2026-07-20: 高保真还原样稿重设计

- **输入**:`prototype/admin-console-redesign.html`(样稿,DocLibrary 部分)
- **输出**:5 个视图(OverviewPage 新增 / DocManagePage 重写 / DocDetailPage 新增 / ParseResultsPage 重写 / SearchPage 重写)+ 8 个组件(Sidebar / Topbar / AppDrawer / AppModal / AppToast / ChartLine / ChartDonut / StatCard / StatusStrip)+ 3 个 composables(useToast / useCountUp)+ 1 个 icons 工具
- **样式**:`tokens.scss`(CSS 变量)+ `app.scss`(组件类库)+ `element-variables.scss`(EP 主色 `#4F46E5`)+ `style.css`(排版 helper)
- **路由**:Hash 路由(4 主页 + detail 子页),provide/inject 跨层级通信
- **业务逻辑零改动**:`git diff` 纯展示层,API 端点/参数/校验/轮询/确认框完全等价(自查见 `Redesign-2026-07-Admin-Console.md` §业务零改动自查清单)
- **设计文档**:`docs/overview/Redesign-2026-07-Admin-Console.md`(权威规格,含 design token / 组件模式 / 文件清单 / 业务零改动自查)
- **EP 保留范围**:el-input / el-select / el-input-number / el-pagination / el-switch / el-checkbox / ElMessageBox(表单与分页控件);其余展示层组件(button / card / table / drawer / modal / toast / sidebar / topbar / charts)全部手写以达高保真

### 2026-07-04: 移除 Identity 鉴权

- 删除 `LoginPage.vue`、`authService.ts`(无登录页、无 Token 存储)
- `App.vue` 移除登录检查、用户区域、登出按钮
- `docApi.ts` 改用普通 axios,移除 `createAuthenticatedClient`
- 后端移除 `AuthEndpoints.cs`、JWT Bearer 中间件、IdentityService HttpClient
- `.gitignore` 移除 `authService.js` 条目

### 2026-07-10: ADR-001 迁移到 Element Plus

- 重新引入 element-plus ^2.10.0 / @element-plus/icons-vue ^2.3.1 / sass ^1.80.0 / unplugin-auto-import ^0.18.3 / unplugin-vue-components ^0.27.4
- vite.config.ts 启用 AutoImport + Components(ElementPlusResolver,importStyle: false)
- 新增 `src/styles/element-variables.scss`(@forward + @use 注入品牌 SCSS token)
- main.ts 引入 element-variables.scss 编译全量 themed CSS
- 保留 minerU 第 2 代检索能力(DS-13~DS-18):8 个 minerU 过滤参数 + 行展开 blockData 详情 + mineruScore 独立展示

### 2026-07-03: UI 重构

- 移除 element-plus / @element-plus/icons-vue / marked / @types/marked 依赖
- style.css 清理约 600 行死代码,统一 CSS 变量命名
- (注:此次重构后被 ADR-001 部分回滚,再被 2026-07-20 重设计进一步演进)
