# ADR-001：DocLibrary 管理前端引入 Element Plus 并替换纯手写 CSS

## 状态

已接受（Accepted） — 2026-07-10

## 背景

DocLibrary 管理前端最初使用 914 行纯手写 `style.css` + 三页面手写组件类（`.btn` / `.card` / `.data-table` 等），仅依赖 `vue + axios`。该决策（记录在 `frontend/docs/frontend-spec.md` §2）在当时合理——组件最少、能跑。

经过 minerU 第 2 代检索能力上线（SearchPage.vue 高级筛选 + 展开 + 响应式多列），我们确认纯手写方案在以下维度**达不到"真正产品级"**：

1. **交互精致度**：loading skeleton / 行 hover 过渡 / 表单校验联动 / Drawer / 空态插图等微动效需要大量手写 JS + CSS transition，且每次页面复制都会**产生微妙不一致**（"半成品 demo 感"的主要来源）。
2. **跨端一致性**：手机 + PC + 平板三档响应式，手写栅格在每个页面都要重新排，无法保证间距、字号、断点一致。
3. **缺陷成本**：每次加新组件都要补齐 hover / focus / active / disabled / loading / 空态六种稳定态，手写组件类的稳定态遗漏是 bug 温床。
4. **项目趋势**：admin_portal、teacher_portel 已在使用 Element Plus；doclibrary 是门户管理员高频入口，视觉/交互**若能与既有门户统一**将降低用户认知成本。

## 决策

**引入 Element Plus 作为 doclibrary 管理前端唯一 UI 组件库，迁移三页面（DocManagePage / ParseResultsPage / SearchPage）+ App.vue 布局壳，同时把现有 CSS 变量主题体系转为 Element Plus SCSS design token 源。**

### 动机优先级

| 优先级 | 目标 |
|--------|------|
| P0 | 交互精致（精致过渡、表单联动、稳定态完备）|
| P1 | PC + 移动 + 平板三档响应式 |
| P2 | 跨项目门户视觉统一 |

### 范围（一次迁完，不留半套）

- ✅ 全部三页面 + App.vue 布局壳（侧栏 / 顶栏 / 面包屑 / 响应式抽屉）
- ✅ 基础组件全面替换为 EP：Button / Input / Select / Checkbox / Switch / Table / Drawer / Pagination / Tag / Skeleton / Empty / Message / MessageBox / Popconfirm
- ✅ 现有 `style.css` 914 行提炼为 EP SCSS design token 映射（见 §5），旧 `style.css` 拆成小幅覆写文件
- ❌ 不动 admin_portal / teacher_portal（统一后续单独评估，本 ADR 不包）
- ❌ 不动后端（API 契约 / minerU 检索 / 数据库 0 改动）

### 技术栈变更

迁移前：
```
Vue 3.5 + TypeScript + Vite + 纯手写 CSS (style.css 914 行) + axios
```

迁移后：
```
Vue 3.5 + TypeScript + Vite + Element Plus (按需导入, unplugin-auto-import 或手动)
  + EP SCSS design token 覆写 (来自现有 style.css 变量)
  + axios (保留)
```

## 选型理由

### 为何不是 Naive UI / Arco Design / Ant Design Vue

| 方案 | 评估 |
|------|------|
| **Element Plus** ✅ 选用 | 成熟稳定，admin 场景验证最多；Vue 3.5 原生；按需导入支持好；admin_portal/teacher_portal 已用 = 立即统一 |
| Naive UI | TS 原生、bundle 小、设计更现代；但生产环境踩坑少、issue 响应慢于 EP、手动迁移已有门户成本更高 |
| Arco Design | admin 专精、组件精致；bytes 风格与项目现有不同；社区小于 EP |
| Ant Design Vue | 最成熟 admin 库；bundle 重、默认风格饱和；迁移已有门户成本更高 |
| 纯手写（保持现状） | 当前路径 — 交互精致 + 三端一致 + 稳定态 = 工数持续高于补齐 EP 一次性成本；不可接受 |

### 稳定优先 > 审美新颖

"精致"在这个项目的真正含义是：**过渡平滑、状态完整、跨端一致、bug 少**——Element Plus 在四项上均优于更年轻的 Naive/Arco。审美可通过 token 覆写单独调整。

## 主题策略（CSS 变量 → EP SCSS 映射）

现有 `style.css :root` 的 CSS 变量继续作为**品牌色 / 间距 / 圆角 / 过渡** 的唯一来源，不再维护两套 token。

### 变量映射表（CSS 变量 → EP SCSS 变量）

| 现有 CSS 变量 | EP SCSS 变量 | 说明 |
|-------------|-------------|------|
| `--primary-color: #2563eb` | `$--color-primary` / `$--color-primary-light-3~9` 系列 | 主色阶梯由 `#2563eb` 自动生成 |
| `--primary-dark: #1d4ed8` | `$--color-primary-dark-2` | 悬停色 |
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
| `--radius-sm/md/lg` | `$--border-radius-small/medium/large`（需自定义）| EP 默认更小，保留 4/6/8 |
| `--transition-fast: 150ms ease` | 自定义：覆写 transition-duration（EP 默认 0.3s，需收紧）| |
| `--transition-base: 200ms ease` | | |

### SCSS token 覆写文件（`src/styles/element-variables.scss`）

```scss
// 在 EP 默认变量之前注入品牌值
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
    '':       0 4px 6px -1px rgba(0,0,0,0.1), 0 2px 4px -1px rgba(0,0,0,0.06),
  ),
  $transition-duration: (
    '':     0.2s,
    'fast': 0.15s,
  ),
);

// 显式引入 EP 基础样式
@use 'element-plus/theme-chalk/src/index.scss' as *;
```

### 过渡收紧（EP 默认 0.3s → 现有 0.15/0.2s）

EP 默认动效较长（0.3s）。按要求"精致不拖沓"，覆写全局过渡变量：

```scss
:root {
  --el-transition-duration: 0.2s;
  --el-transition-duration-fast: 0.15s;
}
```

### 保留 `@use` 而非 `@import`

EP v2.7+ 推荐 `@use`，避免 `@import` 在 Vite 中的 future-deprecation 警告。

## 迁移实施要点

### 按层迁移（页面 → 布局 → 微调）

1. **元素级替换**：`btn` / `card` / `input-wrap` / `data-table` / `tag` / `empty-state` → EP 组件
2. **表格替换**：`.data-table` → `el-table` + `el-pagination`；zebra / hover / sticky header / 列选择器沿用
3. **表单替换**：所有筛选区输入 → `el-input` / `el-select` / `el-checkbox` / `el-date-picker`；校验联动用 EP `el-form`
4. **布局替换**：App.vue 的 sidebar 手工 CSS → `el-menu`（折叠 + 响应式抽屉）；顶栏面包屑 → `el-breadcrumb`；空态 → `el-empty`；文字 toast → `el-message` / `el-message-box`
5. **响应式接管**：PC/平板/手机三档由 `el-row` + `el-col` 的断点（`sm / md / lg / xl`）+ 侧栏 `v-if="!isMobile"` 联动，不再手写媒体查询

### 不得遗漏的"精致"细节

- **Skeleton 骨架屏**：表格加载用 `el-skeleton`，替代"搜索中..."文字
- **Empty 统一**：空态全部用 `el-empty`（描述文案统一）
- **Message / MessageBox 统一**：成功/失败反馈用 `el-message`；破坏性操作用 `el-message-box`（`confirmDelete` 当前是 `window.confirm`）
- **Drawer 抽屉**：高级筛选抽屉用 `el-drawer`（当前 SearchPage 已是 inline 折叠 → 改 Drawer）
- **表格行 hover / stripe**：`el-table` `stripe` + `row-class-name` 控制
- **分页**：`el-pagination`（layout 含 `sizes` / `prev` / `pager` / `next` / `jumper` / `total`）

### 保留现有品牌的 Checklist

- [ ] 主色 #2563eb（EP `$--color-primary` 正确注入）
- [ ] 阴影 `--shadow-sm` / `--shadow-md`（EP `$--box-shadow` 映射）
- [ ] 圆角 4/6/8px（EP `$--border-radius` 自定义）
- [ ] 字号阶梯（EP `$--font-size-*` 沿用默认，不再另起）
- [ ] 过渡时长收紧到 0.15/0.2s（EP `$--transition-duration` 覆写）
- [ ] 字体栈：保持 `-apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC'...`（只在 `:root` 续写，不动）

## 后果

### 正向
- 三端稳定（EP 内置响应式断点 + 折叠菜单）
- 稳定态齐全（hover / focus / active / disabled / loading / 空态 / 错误态由 EP 内部实现，不再手写）
- 跨门户统一（与 admin_portal/teacher_portal 共享类名、尺寸、间距体系）
- 手工样式表从 914 行降到纯 token 覆写 + 少量 scoped 覆盖（估计 < 150 行）

### 风险与缓解

| 风险 | 缓解 |
|------|------|
| EP bundle 增大 → 首屏加载变慢 | 按需导入 `unplugin-element-plus` / 手动局部 import；首屏 < 200KB gzip |
| 旧 style.css 914 行一次性迁移易遗漏状态 | 逐表替换：先映射表头 → 再替换表格体 → 再补空态/加载态；每替换一页跑 `npm run build` |
| 主题色注入失败导致返回 EP 默认蓝 | build 后在浏览器核对主色 === #2563eb；`npm run dev` 本地预检 |
| 表单校验联动（docApi 错误码格式）与 EP `el-form` 校验格式不兼容 | 显式把 axios 错误码映射到 EP 校验 message，不在 EP 规则层硬接 |
| 迁移期间 minerU 检索新增字段（blockData/minerUscore/subType 等）需要重测表格列 | SearchPage.vue 已加 minerU 列，EP 迁移后必须保留且展示正常 |

### 工作量估计

- 基础替换（元素+表格+表单）：1 周
- 响应式 + 精致态（skeleton / drawer / message / transition）：3 天
- 主题 token 覆写 + 视觉微调：2 天
- 三页面回归测试：2 天

总计约 2 周。

## 参考

- EP 官方主题文档：https://element-plus.org/zh-CN/guide/theming.html
- EP SCSS 变量全量：https://github.com/element-plus/element-plus/blob/dev/packages/theme-chalk/src/common/var.scss
- 既有门户 EP 实践：`src/admin_portal/frontend`（Element Plus 已接入）
- 本前端既有样式源：`frontend/src/style.css`（CSS 变量见 §5 映射表）
- minerU 第 2 代检索设计：`docs/modules/DocumentSearch/03-DESIGN.md §演进（DS-13~DS-18 已合并 master）`
