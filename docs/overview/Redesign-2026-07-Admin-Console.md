# DocLibrary Admin 前端重设计（2026-07）

## 状态

已接受（Accepted） — 2026-07-20

## 背景

依据仓库根目录 `prototype/admin-console-redesign.html` 设计样稿，对 DocLibrary 管理前端进行视觉与交互层的高保真还原。本次重设计**仅做展示层还原**，不修改任何业务逻辑、API 契约、状态管理与数据流（铁律：业务逻辑零改动）。

样稿同时包含 Identity（身份中心）与 DocLibrary（文档库）两套界面，但**本次落地范围仅限 DocLibrary**——目标工程为 `src/services/ruoyu.doclibrary/frontend/`，不涉及 Identity 服务。样稿中 Identity 部分留给后续单独评估。

## 设计规格清单（提炼自样稿）

### Design Token

| Token | 值 | 用途 |
|------|----|----|
| `--primary` | `#4F46E5` | 主色（靛蓝，替换原 `#2563eb`） |
| `--primary-hover` | `#4338CA` | 主色悬停 |
| `--primary-soft` | `#EEF2FF` | 主色浅底 |
| `--primary-line` | `#C7D2FE` | 主色边框 |
| `--ink-0/1/2` | `#0D0F1E / #141731 / #1D2142` | 侧栏深色背景三阶 |
| `--ink-text/2/3` | `#E7E9F5 / #9BA1C4 / #5D6388` | 侧栏文字三阶 |
| `--bg` | `#F5F6F8` | 页面底色 |
| `--surface` | `#FFFFFF` | 卡片底色 |
| `--surface-2` | `#F8F9FC` | 次级面板底色 |
| `--border` | `#E5E7EF` | 主边框 |
| `--border-2` | `#EEF0F6` | 表格分行线 |
| `--text/2/3` | `#161927 / #586076 / #9AA0B6` | 文字三阶 |
| `--success` 系列 | `#10B981 / #ECFDF5 / #A7F3D0` | 绿色语义 |
| `--warning` 系列 | `#D97706 / #FFFBEB / #FDE68A` | 黄色语义 |
| `--danger` 系列 | `#EF4444 / #FEF2F2 / #FECACA` | 红色语义 |
| `--info` 系列 | `#0EA5E9 / #F0F9FF / #BAE6FD` | 蓝色语义 |
| `--r-card` | `12px` | 卡片圆角 |
| `--r-btn` | `8px` | 按钮圆角 |
| `--r-input` | `6px` | 输入框圆角 |
| `--shadow-hover` | `0 6px 16px -6px rgba(22,25,39,.12), 0 2px 4px -2px rgba(22,25,39,.06)` | 卡片 hover |
| `--shadow-float` | `0 16px 40px -12px rgba(22,25,39,.18), 0 4px 10px -4px rgba(22,25,39,.08)` | 抽屉/弹窗 |
| `--ease` | `cubic-bezier(.22,.61,.36,1)` | 标准过渡曲线 |
| `--spring` | `cubic-bezier(.34,1.35,.44,1)` | 弹性过渡（指示器/弹窗入场） |
| `--mono` | `ui-monospace,"SF Mono","Cascadia Code","JetBrains Mono",Consolas,monospace` | 等宽字体 |

字号层级：页面标题 23px / 卡片标题 14.5px / 表格正文 13.5px / 辅助文字 12~12.5px / 表头 11.5px。

### 页面清单与信息架构（仅 doclib 部分）

```
Sidebar（深色 ink 渐变）
  - Brand mark「若」+「若愚学习平台 / 管理控制台」
  - Nav（顶部小标签 ruoyu.doclibrary）
    - 概览   (grid)
    - 文档管理 (file)
    - 检索测试 (search)
  - Sidebar-foot：admin chip（avatar + name + role）—— 本工程无 Identity，仍展示静态「admin / 文档库管理员」
Topbar
  - 面包屑：文档库 › 当前页
  - env-tag「内网环境」+ 实时时钟（每秒更新）
Main（#view，200ms 模糊过渡）
  - 概览页 / 文档管理 / 文档详情 / 检索测试
```

> **系统切换器（sys-switch）不展示**：样稿的双系统切换用于演示 Identity ↔ DocLibrary 切换。本工程仅承载 DocLibrary，无需切换器，保留 nav-label「ruoyu.doclibrary」即可。

### 组件模式

- **stat-card**：4 列网格，数字 29px tabular-nums，hover 上浮 2px + shadow-hover
- **strip**（状态条）：4 列网格，活跃态 primary 边框 + 3px ring
- **card**：12px 圆角，1px border，padding 24px；card-head 18px 下边距
- **badge**：6 档色（gray/green/amber/red/blue/indigo），含 5px dot + 23px 高度
- **chip**：29px 高度圆角胶囊，active 态 primary-soft 底
- **btn**：34px 高度，主/ghost/danger/sm 四档
- **input/select**：34px 高度，focus 3px primary ring
- **switch**：34x20 自定义 toggle（不用 EP el-switch 默认视觉，但功能等价）
- **table**：表头 11.5px 大写 letter-spacing，tbody 行 hover 浅底；clickable 行 cursor pointer
- **drawer**：右侧 520px，spring 入场（translateX 36→0 + opacity 0→1），200ms 模糊遮罩
- **modal**：460px scale + translateY 入场（spring 曲线）
- **toast**：右上角深色通知，spring 入场
- **pager**：右对齐，current 页 primary 底白字
- **upload-zone**：1.5px 虚线边框，hover/drag primary-soft 底
- **chart**：SVG 折线（Catmull-Rom 平滑路径）+ donut（stroke-dasharray 描画动画）
- **progress-track**：5px 高度，linear-gradient(90deg, #818CF8, #4F46E5) 填充

### 动效清单

- 数字滚动：`runCounters` cubic ease-out 900ms，tabular-nums
- SVG 折线描画：stroke-dashoffset 1.1s ease
- SVG donut 描画：stroke-dasharray 0.9s ease，逐段 0.12s 错峰
- 卡片 hover：translateY -2px + shadow-hover，220ms ease
- 抽屉入场：translateX 36→0 + opacity，300ms ease
- 弹窗入场：scale(.96)→1 + translateY 8→0，260ms spring
- Toast 入场：translateX 24→0 + opacity，300ms spring
- 视图切换：#view opacity 0 + blur 6px + translateY 5px，150ms ease（leaving）→ 切换 → 恢复
- 侧栏 nav-indicator 滑动：translateY 跟随当前 active 项，340ms spring

## 现有工程映射

| 样稿页面 | 现有工程文件 | 改造类型 |
|---------|------------|---------|
| 概览（pgDocOverview） | — | **新增页面** `OverviewPage.vue` |
| 文档管理（pgDocDocs） | `views/DocManagePage.vue` | 改造（状态条 + 上传区 + 表格视觉） |
| 文档详情（pgDocDetail） | — | **新增页面** `DocDetailPage.vue` |
| 检索测试（pgDocSearch） | `views/SearchPage.vue` | 改造（卡片式结果 + dev 模式） |
| 解析结果 | `views/ParseResultsPage.vue` | 用样稿设计语言重新包装（保留所有按钮） |
| 布局壳 | `App.vue` | 完全重做（深色侧栏 + nav-indicator + 时钟） |
| 全局样式 | `style.css` + `styles/element-variables.scss` | 新增 design token，主色由 `#2563eb` → `#4F46E5` |

### 新增页面数据来源（不动 API）

- **概览页**：调用现有 `listDocumentFiles(page=1, pageSize=100)` 与 `listDocumentParses(page=1, pageSize=100)`，在前端按 `parseStatus` 聚合得到状态分布甜甜圈，按 `parsedAt` 日期分桶得到近 30 天解析吞吐折线，取 `listDocumentFiles` 前端排序的前 5 条作为「最近上传」。统计超过 100 条样本时存在截断误差，已在文档中标注。
- **文档详情页**：使用 `getDocumentFile(id)`，展示文件基础信息 + parses 列表（含 markdown/HTML/JSON/V2/Model/Layout/图片 按钮，行为同 ParseResultsPage，但改为抽屉式预览）。点击「重新解析」调用 `parseDocumentFile`；「开始解析」同。

### 样稿有但本次不实现（说明原因）

| 样稿元素 | 原因 |
|---------|------|
| 学科/年级/年份元数据卡片（pgDocDetail 右上） | API `DocumentFile` 无 `subject/grade/year` 字段；为避免静态皮，**省略该卡片**。后端若后续扩展 `DocumentMetadata` 模块，再补回。 |
| 下游流向卡片（pgDocDetail 右下） | OpenSearch 同步状态 / QuestionBank 导入按钮均无对应 API；**省略**。 |
| 题库导入 toast 演示 | 样稿为 `App.toastDemo(...)` mock；无对应 API，**不实现**。 |
| 系统切换器 sys-switch | 本工程无 Identity；**不展示**。 |
| Identity 全部页面 | 不在范围；**不实现**。 |

### 样稿没有但工程保留

- **ParseResultsPage**（解析结果列表页）：样稿将解析记录收敛进文档详情，但本工程历史版本提供独立列表入口。**保留**该页，用样稿设计语言重新包装（深色侧栏 + 卡片 + EP 组件 + 统一 token）。删除会破坏既有功能入口，违反"禁止删除样稿没有但业务上存在的功能入口"。
- **8 个 minerU 高级筛选参数**（blockType/subType/pageNumber/textLevel/textFormat/parseId/documentFileId/hasImage）：保留，迁移到抽屉式高级筛选。
- **行展开 blockData 详情**：保留，迁移到结果卡片的「展开详情」交互。

## 技术映射策略

- 主色由 `#2563eb` → `#4F46E5`，覆写 `element-variables.scss` 的 `$colors.primary.base`
- 新增 `src/styles/tokens.scss` 集中存放样稿 design token（CSS 变量），供 scoped 样式引用
- 不引入图表库——样稿 SVG 图表用 Vue + 原生 SVG 重写（与样稿相同的描画动画逻辑）
- 侧栏手写深色背景 + nav-indicator 滑动；不使用 EP el-menu 默认浅色样式（深色样式 EP 默认不匹配，手写更直接）
- 顶栏手写：sticky + backdrop-blur + 时钟
- 按钮样式手写为 `.btn / .btn-ghost / .btn-danger / .btn-sm`（与样稿同名），不强制走 EP el-button 默认视觉
- Drawer / Modal / Toast 手写，与样稿视觉一致；不使用 EP el-drawer/el-dialog/el-message 默认视觉
- 表格手写为样稿 `.table-wrap > table` 风格，仍走 v-loading 指令
- Pagination 手写为 `.pager` 风格，保留 EP el-pagination 作为底层分页能力

> **决策**：本工程 ADR-001 已引入 EP 并完成迁移。本次重设计在保留 EP 作为基础组件库的前提下，对**核心展示组件**（按钮、卡片、表格、抽屉、弹窗、Toast）采用样稿同名手写类，因为样稿的视觉细节（圆角档位、阴影档位、动效曲线、nav-indicator 滑动、SVG 描画动画）用 EP 默认 token + scoped 覆写难以达到高保真，强行覆盖会产生大量深层选择器。EP 仍用于 `el-input/el-select/el-pagination/el-checkbox/el-switch` 等表单与分页能力，避免重复造轮子。

## 修改文件清单

### 新增

| 文件 | 用途 |
|------|------|
| `frontend/src/styles/tokens.scss` | Design Token CSS 变量 |
| `frontend/src/components/Sidebar.vue` | 深色侧栏（nav-indicator 滑动） |
| `frontend/src/components/Topbar.vue` | 顶栏（面包屑 + 时钟 + env-tag） |
| `frontend/src/components/ChartLine.vue` | SVG 折线图（描画动画） |
| `frontend/src/components/ChartDonut.vue` | SVG 甜甜圈图（描画动画） |
| `frontend/src/components/AppDrawer.vue` | 通用抽屉（spring 入场） |
| `frontend/src/components/AppModal.vue` | 通用弹窗（spring 入场） |
| `frontend/src/components/AppToast.vue` | Toast 容器（spring 入场） |
| `frontend/src/components/StatCard.vue` | 统计卡（hover 上浮 + 数字滚动） |
| `frontend/src/components/StatusStrip.vue` | 状态条 |
| `frontend/src/composables/useToast.ts` | Toast 调用 composable |
| `frontend/src/composables/useCountUp.ts` | 数字滚动 composable |
| `frontend/src/views/OverviewPage.vue` | 概览页 |
| `frontend/src/views/DocDetailPage.vue` | 文档详情页 |
| `frontend/src/views/ParsePreviewDrawer.vue` | 解析产物预览抽屉（Markdown/图片/JSON 三 tab） |
| `frontend/src/router/index.ts` | 简易 hash 路由（#docs/:id / #search 等） |

### 修改

| 文件 | 修改内容 |
|------|---------|
| `frontend/src/App.vue` | 完全重做布局壳：深色 Sidebar + Topbar + view 容器（200ms 模糊过渡） |
| `frontend/src/main.ts` | 引入 tokens.scss |
| `frontend/src/style.css` | 保留排版 helper，移除与 token 冲突的项 |
| `frontend/src/styles/element-variables.scss` | 主色改为 `#4F46E5`，过渡时长按样稿 |
| `frontend/src/views/DocManagePage.vue` | 加状态条 + 上传区 + 表格视觉重写 |
| `frontend/src/views/ParseResultsPage.vue` | 用样稿设计语言重写视觉 |
| `frontend/src/views/SearchPage.vue` | 卡片式结果 + dev 模式 dev-block + 抽屉高级筛选 |

### 不动

- `frontend/src/services/docApi.ts`（API 客户端，零改动）
- `frontend/src/utils/format.ts`（已有 helper，沿用）
- `frontend/package.json` / `vite.config.ts` / `tsconfig*.json`（构建配置不动）
- 后端任何文件

## 业务逻辑零改动自查清单

实施完成后 `git diff` 必须满足：

- [ ] `docApi.ts` 0 行变更
- [ ] 所有 API 端点（`/admin/document-files/*`、`/admin/document-parses/*`、`/admin/documents/search`）调用与参数完全等价
- [ ] `parseDocumentFile(id, modelVersion)` 仍传 `'vlm'` 或 `'pipeline'`
- [ ] `searchTest` 仍传全部 8 个 minerU 过滤参数
- [ ] 轮询逻辑（5s 间隔 + 解析完成自动停止）保留
- [ ] 上传进度回调逻辑保留
- [ ] 删除确认（ElMessageBox）保留
- [ ] 文件详情解析记录获取仍走 `getDocumentFile(id)`，不引入新端点
- [ ] Markdown / HTML / JSON / V2 / Model / Layout / 图片 打开行为等价（仅改为抽屉式预览，调用的 API 相同）
- [ ] 导出 Markdown / HTML（blob 下载）行为等价

## 验证

1. `npm run build`（vue-tsc + vite build）零错误零警告
2. 改造前后截图比对（视觉一致度 ≥ 90%）
3. 既有功能逐项回归：上传 / 解析触发 / 轮询 / 删除 / 检索 / 高级筛选 / 行展开 / 导出
4. `git diff` 不含 API 端点 / 请求参数 / store 逻辑变更
