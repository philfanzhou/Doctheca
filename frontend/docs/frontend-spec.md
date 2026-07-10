# DocLibrary Admin 前端规格

## 1. 概述

DocLibrary Admin 是文档库的管理后台前端,供本地管理员上传文档、触发 MinerU 解析、查看解析结果、测试检索功能。

- 技术栈:Vue 3.5 + TypeScript + Vite + Element Plus(SCSS design-token 覆写,按 ADR-001 §5)
- 部署:由 DocLibrary 后端 Host 静态托管(`wwwroot/`),与后端同源,无需独立部署
- 访问:HTTP `:5012`

## 2. 依赖策略

**Element Plus 作为唯一 UI 组件库,纯手写 CSS 改为 SCSS design-token 覆写。**

迁移令：ADR-001（docs/overview/ADR-001-frontend-element-plus.md）。

| 依赖 | 用途 | 必要性 |
|------|------|--------|
| `vue` | 框架(3.5) | 必需 |
| `typescript` | 类型(~) | 必需 |
| `vite` | 构建 | 必需 |
| `axios` | HTTP 请求 | 必需 |
| `element-plus` | UI 组件库 | 必需 |
| `element-plus/theme-chalk` | SCSS 主题覆写源 | 必需(按 ADR-001 §5 映射表) |
| `sass` | SCSS 编译(element-plus 主题必需) | 必需(迁移期间引入) |

**历史决策(已撤销):**
- `element-plus` / `@element-plus/icons-vue` — 曾在早期被移除,本次由 ADR-001 重新引入
- `marked` — 未使用(解析结果 Markdown 以 `<pre>` 原文展示,不渲染)

**不在依赖内:**
- 其他 UI 库(Naive / Arco / Ant Design Vue / Vuetify) — ADR-001 已排除
- `element-plus` 图标如用 iconfont 单独处理,否则使用 `@element-plus/icons-vue`(按需)

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
┌─────────────────────────────────────────┐
│ Sidebar │  TopHeader(面包屑)            │
│  - Logo ├───────────────────────────────┤
│  - 导航  │  ContentArea                  │
│    文档管理│  (动态组件:DocManagePage /   │
│    解析结果│   ParseResultsPage /         │
│    检索测试│   SearchPage)                │
└─────────────────────────────────────────┘
```

- 侧边栏可折叠(桌面 ≥768px,el-aside + el-menu :collapse)/ 抽屉式(移动端 <768px,el-drawer direction="ltr")
- 折叠状态持久化:localStorage(`docSidebarCollapsed`)
- 导航项:文档管理、解析结果、检索测试
- 无登录页、无用户区域、无登出按钮

### 4.2 DocManagePage(文档管理)

- 功能:上传文件(PDF/DOCX/PPT)、查看文件列表、触发解析(VLM/Pipeline)、删除文件
- 上传进度条
- 文件列表表格:文件名、类型、上传时间、解析状态、操作
- 解析中状态自动轮询(5s 间隔,无活跃任务时停止)
- 分页

### 4.3 ParseResultsPage(解析结果)

- 功能:查看所有解析记录、查看 Markdown/HTML/JSON/V2/Model/Layout/图片、导出 MD/HTML、删除解析
- **VLM 和 Pipeline 模式统一显示所有按钮**(按钮不再按 modelVersion 过滤)
- 按钮显示逻辑:status=parsed 时显示操作按钮;点击 JSON/V2/Model/Layout 按钮时,从详情接口获取数据,有值则新窗口展示,无值则 toast 提示
- 分页

### 4.4 SearchPage(检索测试)

- 功能:关键词检索 + 高级筛选(minerU 字段)双区 + 结果表格(V1 列 + minerU 列) + 行展开 blockData 详情
- **V1 关键词区**(保留):关键词输入框 + 短语查询开关 + 搜索按钮
- **[Gen-2] 高级筛选抽屉**(可折叠,默认收起):
  - blockType(datalist 候选:text/title/image/table/chart/list/interline_equation/code/algorithm/equation/phonetic/ref_text/header/footer/page_number/aside_text/page_footnote)
  - blockSubType(自由输入,如 table_caption / code / algorithm)
  - pageNumber(0-based page_idx)、textLevel(0=正文/1=h1/2=h2)
  - textFormat(latex/markdown/none)、parseId、documentFileId、hasImage
  - "应用筛选" / "清空筛选" 按钮;所有 minerU filter 可选,空值不透传(零回归)
- **结果表格**(V1 列保留 + minerU 列追加):
  - V1 列:#、文档标题、页码(显示 page_idx+1)、匹配类型、BM25 相关度、匹配文本、Segment ID、创建时间
  - minerU 列:块类型(从 blockData.type 解析)、subType、矿工 U 置信度(mineruScore)、textFormat、Caption(前 30 字)
  - 操作列:"展开详情"按钮 → 行内展开卡片展示 blockData 格式化 JSON + bbox[text0,y0,x1,y1] + mineruScore 与 V1 Score 明确区分标签
- **状态**(ADR-001 迁移后):空结果由 `hasSearched` 标志位控制——仅点击搜索按钮执行过检索后才显示"无匹配结果";输入过程中(未搜索)显示"输入查询词后点击搜索"提示。loading 用 `v-loading` 指令;错误用 `ElMessage.error`

## 5. 样式规范

### 5.1 全局样式(style.css)

ADR-001 迁移后,`src/style.css` 仅保留:
- `:root` 品牌色 / 间距 / 圆角 / 过渡 token(作为 EP SCSS 的补充,EP 变量权威源在 `src/styles/element-variables.scss`)
- ADR §5.3 过渡收紧:`--el-transition-duration: 0.2s` / `--el-transition-duration-fast: 0.15s`
- 基础重置(box-sizing / body / #app)
- 排版辅助类:`.page-header` / `.page-title` / `.page-subtitle`(三页面共用)

> 手写组件类(`.btn` / `.card` / `.data-table` / `.tag` / `.status-badge` / `.pagination-bar` / `.input-wrap` / `.sidebar` / `.empty-state` / `.upload-progress` 等)已全部移除,功能由 Element Plus 组件承担。页面级布局类在各 `.vue` 文件 `<style scoped>` 内定义。**禁止内联 `style="..."`**(进度条等动态宽度除外,用 `:style` 绑定)。

### 5.2 EP SCSS 主题覆写(element-variables.scss)

按 ADR-001 §5.2 模板,`src/styles/element-variables.scss` 通过 `@forward 'element-plus/theme-chalk/src/common/var.scss' with (...)` 注入品牌值,并 `@use 'element-plus/theme-chalk/src/index.scss'` 一次性编译完整主题样式。`main.ts` 引入一次即生效全量 themed CSS。

| EP SCSS 变量 | 品牌值 | 对应旧 CSS 变量 |
|------|------|------|
| `$colors.primary.base` | `#2563eb` | `--primary-color` |
| `$colors.success/danger/warning/info.base` | `#10b981` / `#ef4444` / `#f59e0b` / `#3b82f6` | 语义色 |
| `$text-color.primary/regular/secondary` | `#111827` / `#6b7280` / `#9ca3af` | 文字色阶 |
| `$border-color.base/light` | `#e5e7eb` / `#f3f4f6` | 边框色 |
| `$bg-color.(''/page/overlay)` | `#f5f7fa` / `#f5f7fa` / `#ffffff` | 背景色 |
| `$border-radius.small/medium/large` | `4px` / `6px` / `8px` | 圆角 |
| `$box-shadow.light/''` | 品牌阴影 | `--shadow-sm/md` |
| `$transition-duration.(''/fast)` | `0.2s` / `0.15s` | 过渡收紧(ADR §5.3) |

> 运行时 EP CSS 变量(`--el-color-primary` / `--el-text-color-secondary` / `--el-border-color-lighter` / `--el-fill-color-light` 等)由 themed SCSS 编译产出,页面 scoped 样式可直接引用。

### 5.3 组件映射(手写类 → Element Plus)

ADR-001 迁移后,所有手写组件类替换为 EP 组件:

| 旧手写类 | Element Plus 组件 |
|------|------|
| `.btn` / `.btn-primary` / `.btn-secondary` / `.btn-danger` / `.btn-link` / `.btn-small` | `ElButton`(type / size / text / loading / disabled) |
| `.input-wrap input` / `.select-wrap select` | `ElInput` / `ElInputNumber` / `ElSelect` |
| `.checkbox-wrap` | `ElCheckbox` / `ElSwitch` |
| `.card` / `.card-header` / `.card-body` | `ElCard` + `#header` slot |
| `.data-table` | `ElTable` + `ElTableColumn`(stripe / border / show-overflow-tooltip / type="expand") |
| `.pagination-bar` / `.page-btn` | `ElPagination`(layout="total, sizes, prev, pager, next, jumper") |
| `.tag` / `.status-badge` | `ElTag`(type="success/info/warning/danger") |
| `.empty-state` | `ElEmpty` |
| `.spinner` / "加载中..."文字 | `v-loading` 指令 / `ElSkeleton` |
| `.upload-progress` | `ElProgress` |
| `window.confirm` / `alert` | `ElMessageBox.confirm` / `ElMessage` |
| 手写 SVG 图标 | `@element-plus/icons-vue`(Document / Files / Search / Upload / Filter / Menu / Expand / Fold) |
| 高级筛选 inline 折叠 | `ElDrawer`(direction="rtl") |

> 状态映射:`format.ts` 的 `getFileStatusType(status)` 返回 EP `ElTag` type(success/danger/warning/info),替代旧 `getFileStatusClass`。

### 5.4 已清理的死代码

以下 CSS 类已从 style.css 移除(对应功能未实现或已删除):
- Segment Refinement 相关(`.segment-*`、`.split-*`、`.profile-*`)
- 旧版弹窗(`.dialog-*`、`.modal-*`)
- 未使用的过滤器栏(`.filter-bar*`、`.filter-input`)
- 未使用的按钮变体(`.btn-link-success`、`.btn-link-warning`、`.btn-link-danger`、`.btn-success`)
- 未使用的统计卡片(`.stat-card`、`.stat-item`、`.stats-bar*`)
- 未使用的 Markdown 预览(`.markdown-preview`)
- ADR-001 迁移:全部手写组件类(见 §5.3 映射表)

## 6. API 客户端(docApi.ts)

| 方法 | 端点 | 说明 |
|------|------|------|
| `uploadDocumentFile` | `POST /admin/document-files/upload` | 上传文件(multipart) |
| `listDocumentFiles` | `GET /admin/document-files` | 文件列表(分页+筛选) |
| `getDocumentFile` | `GET /admin/document-files/:id` | 文件详情(含 parses + images) |
| `parseDocumentFile` | `POST /admin/document-files/:id/parse` | 触发解析(modelVersion 参数) |
| `deleteDocumentFile` | `DELETE /admin/document-files/:id` | 删除文件 |
| `exportMarkdown` / `exportHtml` | `GET /admin/document-files/:id/export/*` | 导出 |
| `listDocumentParses` | `GET /admin/document-parses` | 解析记录列表 |
| `deleteDocumentParse` | `DELETE /admin/document-parses/:id` | 删除解析记录 |
| `exportParseMarkdown` / `exportParseHtml` | `GET /admin/document-parses/:id/export/*` | 导出解析结果 |
| `searchTest` | `GET /admin/documents/search` | 检索测试 |

## 7. 测试

前端**无单元测试框架**(package.json 未配置 vitest/jest)。验证方式:
1. `npm run build`(vue-tsc 类型检查 + vite 构建)必须通过
2. 人工验证页面功能

后端 UT 不受前端重构影响(前端重构不改变 API 契约)。

## 8. 重构记录

### 2026-07-04: 移除 Identity 鉴权

- 删除 `LoginPage.vue`、`authService.ts`(无登录页、无 Token 存储)
- `App.vue` 移除登录检查、用户区域、登出按钮
- `docApi.ts` 改用普通 axios,移除 `createAuthenticatedClient`
- 后端移除 `AuthEndpoints.cs`、JWT Bearer 中间件、IdentityService HttpClient
- `.gitignore` 移除 `authService.js` 条目
- 详见后端 `docs/overview/Design.md` 与 `docs/overview/Integration.md`

### 2026-07-03: UI 重构

- 移除 element-plus / @element-plus/icons-vue / marked / @types/marked 依赖
- main.ts 移除 ElementPlus 全局注册
- style.css 清理约 600 行死代码,统一 CSS 变量命名
- SearchPage/DocManagePage/ParseResultsPage 消除所有内联样式
- ParseResultsPage 修复 VLM 模式按钮显示(移除 modelVersion === 'pipeline' 限制)
- App.vue 移除未使用的 lastRefreshTime

### 2026-07-10: ADR-001 迁移到 Element Plus

- 重新引入 element-plus ^2.10.0 / @element-plus/icons-vue ^2.3.1 / sass ^1.80.0 / unplugin-auto-import ^0.18.3 / unplugin-vue-components ^0.27.4
- vite.config.ts 启用 AutoImport + Components(ElementPlusResolver,importStyle: false)
- 新增 `src/styles/element-variables.scss`(@forward + @use 注入品牌 SCSS token,见 §5.2)
- main.ts 引入 element-variables.scss 编译全量 themed CSS
- App.vue 改为 el-container / el-aside(el-menu 折叠) / el-header(面包屑) / el-main;移动端 el-drawer
- 三页面(DocManagePage / ParseResultsPage / SearchPage)全部手写组件类替换为 EP 组件(见 §5.3 映射表)
- window.confirm → ElMessageBox.confirm;alert/console.error → ElMessage
- style.css 从 ~914 行清理至 ~89 行,仅保留 :root tokens + 排版辅助类
- 保留 minerU 第 2 代检索能力(DS-13~DS-18):8 个 minerU 过滤参数 + 行展开 blockData 详情 + mineruScore 独立列
- 实施偏差 1:移除 vite.config.ts `additionalData` SCSS 注入(导致 module loop),改用 importStyle: false,themed CSS 由 element-variables.scss 一次性编译(详见 ADR-001 ### 实施偏差 1)
