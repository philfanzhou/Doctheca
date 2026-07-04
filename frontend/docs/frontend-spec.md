# DocLibrary Admin 前端规格

## 1. 概述

DocLibrary Admin 是文档库的管理后台前端,供本地管理员上传文档、触发 MinerU 解析、查看解析结果、测试检索功能。

- 技术栈:Vue 3.5 + TypeScript + Vite(纯手写 CSS,不使用 UI 组件库)
- 部署:由 DocLibrary 后端 Host 静态托管(`wwwroot/`),与后端同源,无需独立部署
- 访问:HTTP `:5012`

## 2. 依赖策略

**仅保留运行时必需依赖,不引入 UI 组件库。**

| 依赖 | 用途 | 必要性 |
|------|------|--------|
| `vue` | 框架 | 必需 |
| `axios` | HTTP 请求 | 必需 |

**已移除的依赖(历史遗留,不再使用):**
- `element-plus` / `@element-plus/icons-vue` — 已移除,改用原生 toast 提示
- `marked` — 未使用(解析结果 Markdown 以 `<pre>` 原文展示,不渲染)

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

- 侧边栏可折叠(桌面)/ 抽屉式(移动端 <1200px)
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

- 功能:输入关键词/短语,调用精确检索 API,展示匹配结果
- 结果表格:序号、文档标题、页码、匹配类型、相关度、匹配文本、Segment ID、偏移量、创建时间
- 短语查询开关

## 5. 样式规范

### 5.1 全局样式(style.css)

所有页面共用 `src/style.css`,定义 CSS 变量和通用组件类。**禁止内联 `style="..."`**(进度条等动态宽度除外,用 `:style` 绑定)。

### 5.2 CSS 变量(统一命名)

| 变量 | 用途 |
|------|------|
| `--primary-color` / `--primary-light` / `--primary-dark` | 主色(蓝) |
| `--success-color` / `--danger-color` / `--warning-color` / `--info-color` | 语义色 |
| `--text-primary` / `--text-secondary` / `--text-muted` | 文字色阶 |
| `--border-color` / `--border-light` | 边框色 |
| `--card-bg` / `--bg-color` / `--hover-bg` / `--bg-secondary` | 背景色 |
| `--radius-sm` / `--radius-md` / `--radius-lg` | 圆角 |
| `--shadow-sm` / `--shadow-md` | 阴影 |
| `--sidebar-width` / `--sidebar-collapsed-width` / `--header-height` | 布局尺寸 |

> 历史问题已修复:统一使用 `--primary-color`(不再混用 `--primary`),补充定义 `--bg-secondary`。

### 5.3 通用组件类

- `.btn` / `.btn-primary` / `.btn-secondary` / `.btn-danger` / `.btn-link` / `.btn-small`
- `.card` / `.card-header` / `.card-body`
- `.data-table` / `.table-actions`
- `.input-wrap` / `.select-wrap` / `.checkbox-wrap`
- `.tag` / `.tag-success` / `.tag-info` / `.tag-warning` / `.tag-danger`
- `.status-badge` / `.status-success` / `.status-error` / `.status-processing` / `.status-pending`
- `.pagination-bar` / `.page-btn`
- `.page-header` / `.page-title` / `.page-subtitle`
- `.empty-state` / `.spinner`

### 5.4 已清理的死代码

以下 CSS 类已从 style.css 移除(对应功能未实现或已删除):
- Segment Refinement 相关(`.segment-*`、`.split-*`、`.profile-*`)
- 旧版弹窗(`.dialog-*`、`.modal-*`)
- 未使用的过滤器栏(`.filter-bar*`、`.filter-input`)
- 未使用的按钮变体(`.btn-link-success`、`.btn-link-warning`、`.btn-link-danger`、`.btn-success`)
- 未使用的统计卡片(`.stat-card`、`.stat-item`、`.stats-bar*`)
- 未使用的 Markdown 预览(`.markdown-preview`)

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
