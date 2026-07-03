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
- `element-plus` / `@element-plus/icons-vue` — 仅 LoginPage 用了 `ElMessage`,已改为原生 toast 提示
- `marked` — 未使用(解析结果 Markdown 以 `<pre>` 原文展示,不渲染)

## 3. 认证流程

前端**不直接调用 Identity gRPC**,而是通过 DocLibrary 后端 BFF 代理:

```
浏览器 → POST /admin/auth/login {username, password}
       → DocLibrary 后端代理 → Identity gRPC GetToken(password grant)
       → 返回 { accessToken, refreshToken, userInfo }
```

- 登录端点:`POST /admin/auth/login`(匿名)
- 刷新端点:`POST /admin/auth/refresh`(匿名)
- 当前用户:`GET /admin/auth/me`(JWT 鉴权)
- 登出端点:`POST /admin/auth/logout`(JWT 鉴权,无状态,前端清本地 token)

Token 存储于 `localStorage`,axios 拦截器自动注入 `Authorization: Bearer` 头,401 时自动刷新。

> **架构约束**:Identity 服务未暴露 HTTP JWT 登录端点(`GetToken` 仅 gRPC),且 AppSecret 是后端机密不可下发前端,因此前端必须经 DocLibrary 后端代理登录,不可绕过。

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
│  - 用户  │                               │
└─────────────────────────────────────────┘
```

- 侧边栏可折叠(桌面)/ 抽屉式(移动端 <1200px)
- 导航项:文档管理、解析结果、检索测试

### 4.2 LoginPage(登录页)

- 居中卡片布局,Logo + 标题 + 用户名/密码表单
- 错误提示:原生 toast(不依赖 UI 库)
- Logo 文字:`DL`(DocLibrary)

### 4.3 DocManagePage(文档管理)

- 功能:上传文件(PDF/DOCX/PPT)、查看文件列表、触发解析(VLM/Pipeline)、删除文件
- 上传进度条
- 文件列表表格:文件名、类型、上传时间、解析状态、操作
- 解析中状态自动轮询(5s 间隔,无活跃任务时停止)
- 分页

### 4.4 ParseResultsPage(解析结果)

- 功能:查看所有解析记录、查看 Markdown/HTML/JSON/V2/Model/Layout/图片、导出 MD/HTML、删除解析
- **VLM 和 Pipeline 模式统一显示所有按钮**(按钮不再按 modelVersion 过滤)
- 按钮显示逻辑:status=parsed 时显示操作按钮;点击 JSON/V2/Model/Layout 按钮时,从详情接口获取数据,有值则新窗口展示,无值则 toast 提示
- 分页

### 4.5 SearchPage(检索测试)

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

## 8. 重构记录(2026-07-03)

- 移除 element-plus / @element-plus/icons-vue / marked / @types/marked 依赖
- main.ts 移除 ElementPlus 全局注册
- LoginPage 改用原生 toast,Logo `DR`→`DL`
- style.css 清理约 600 行死代码,统一 CSS 变量命名
- SearchPage/DocManagePage/ParseResultsPage 消除所有内联样式
- ParseResultsPage 修复 VLM 模式按钮显示(移除 modelVersion === 'pipeline' 限制)
- App.vue 移除未使用的 lastRefreshTime
