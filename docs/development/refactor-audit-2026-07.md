# Doctheca 重构审查清单（2026-07）

> 历史快照（2026-07）：文中 `QuestionBankImportServiceTests.cs` 是 Doctheca 自己的内部集成测试，其后已移除；原 QuestionBank 服务已迁出为外部仓库 Quaestura（ADR-0011）。为保审计记录准确，保留原文件名不改。

## 审计范围

- **后端**：`src/**/*.cs`（排除 obj/、bin/），共 72 个文件
- **前端**：`frontend/src/**/*.vue` / `.ts` / `.scss`（排除 node_modules/），共 25 个文件
- **重点提交**：`7b0dcd10`（前端重设计）、`6fd0e90c`（生命周期修复）、`f4404815`（超时硬编码）

## 后端审计总结

| 维度 | 状态 |
|------|------|
| 文件作用域命名空间 | ✅ 符合 |
| Endpoint 类 `static class` + `Endpoints` 后缀 | ✅ 符合 |
| Worker 继承 `BackgroundService` | ✅ 符合 |
| 私有字段 `_camelCase` | ✅ 符合 |
| `async void` | ✅ 未检测到 |
| 配置类 `Options` 后缀 | ✅ 符合 |
| 未使用 `using` | ✅ `dotnet format` 未报告 |
| **违规总数** | **197** |

### 后端违规分布

| 问题类型 | 数量 | 说明 |
|----------|------|------|
| `async_method_naming` | 96 | 异步方法缺少 `Async` 后缀 |
| `chinese_comment_or_message` | 46 | 注释含中文（日志/异常消息均为英文） |
| `null_forgiving_operator` | 46 | 滥用 `!` 空包容操作符 |
| `multiple_public_types` | 9 | 一个文件多个 public 顶层类型 |

### 后端大文件（≥500 行）

| 文件 | 行数 | 处理方向 |
|------|------|----------|
| `Tests/OpenSearchIndexServiceTests.cs` | 791 | 拆分测试或提取 helper |
| `Tests/QuestionBankImportServiceTests.cs` | 745 | 拆分测试或提取 helper |
| `Service/MinerUFileParseWorker.cs` | 698 | 拆分为 Parsing/ 子类 |
| `Service/OpenSearchIndexService.cs` | 668 | 拆分为 OpenSearch/ 子类 |

### 后端 300–500 行文件

| 文件 | 行数 | 处理方向 |
|------|------|----------|
| `Tests/DocumentFileServiceTests.cs` | 484 | 按场景拆分 |
| `Service/Endpoints/DocumentFileEndpoints.cs` | 414 | 按操作拆分 |
| `Service/DocumentAnalysisService.cs` | 361 | 拆分 prompt/response helper |
| `Service/MinerUPrecisionClient.cs` | 333 | 拆分 options/records |
| `Tests/DocumentParseBlockServiceTests.cs` | 321 | 按场景拆分 |

### 后端附加 analyzer 提示

- `DocumentFileRepository.cs:36` CA1862：`string.Contains` 未使用 `StringComparison`。
- `DocumentAnalysisOptions.cs:43` CA1822：`Temperature` 可标记为 `static`。
- `PdfSplitServiceTests.cs:21` CA1822 / CA1859。
- `DocumentFileDeleteCleanupTests.cs:91` CA1861。

## 前端审计总结

| 维度 | 状态 |
|------|------|
| TypeScript `any` | ✅ 未发现 |
| API 调用一致性 | ✅ 统一使用 axios + docApi.ts |
| 构建/类型检查 | ✅ `vue-tsc --noEmit` 无输出 |
| **违规总数** | **13** |

### 前端违规分布

| 优先级 | 数量 | 类型 |
|--------|------|------|
| P0 | 0 | — |
| P1 | 3 | 逻辑错误 1、安全风险 2 |
| P2 | 5 | 重复代码、全局样式污染、大文件 |
| NIT | 5 | 重复导入、未使用变量、import 位置 |

### 前端 P1 问题

1. **StatusStrip 未实际过滤列表**
   - `src/views/ParseResultsPage.vue:19` / `src/views/DocManagePage.vue:25`
   - `filterStatus` 更新后未参与列表过滤

2. **`document.write` 用于新窗口预览**
   - `src/views/DocDetailPage.vue:157,198,214`
   - `src/views/ParseResultsPage.vue:83,186,205`
   - 已转义，但模式本身存在 XSS 与 opener 劫持风险

3. **`ChartLine.vue` labels 未转义**
   - `src/components/ChartLine.vue:30-38`
   - labels 直接拼入 SVG 并经 `v-html` 渲染

### 前端 P2 问题

4. **跨页面重复工具函数**：`escHtml/escAttr`、`downloadBlob`、`openJsonInNewWindow`、`statusBadgeHtml`、`fileIconCls/fileExtLabel` 等
5. **`app.scss` 全局样式污染**：`*` reset、元素选择器、ID 选择器
6. **大文件**：`app.scss` 1668 行、`SearchPage.vue` 610 行、`DocDetailPage.vue` 571 行、`ParseResultsPage.vue` 448 行
7. **`DocDetailPage` 与 `ParseResultsPage` 预览/导出逻辑大面积重复**

### 前端 NIT

- 重复导入 `inject`（`DocDetailPage.vue`、`DocManagePage.vue`）
- `App.vue` 中 `provide` 导入位置不当
- `App.vue` 中 `toastSuccess` 未使用
- `docApi.ts` 中类型断言可优化

## 最近提交风险

### `7b0dcd10` — 前端重设计

- 新增大文件：`app.scss` 1668 行、`SearchPage.vue` 610 行、`DocDetailPage.vue` 571 行
- 全局样式污染风险
- `document.write` 预览模式
- StatusStrip 过滤交互未实现
- 重复工具函数

### `6fd0e90c` — 生命周期修复

- `RemoteFileConversionService` 注册为 Singleton 包装命名 HttpClient
- `Program.cs` 与构造函数中均配置 `BaseAddress`/`Timeout`，存在重复
- `FileConversionOptions` 注入后仅使用 `Url`

### `f4404815` — 超时硬编码

- `Timeout = TimeSpan.FromSeconds(180)` 在 `Program.cs` 和 `RemoteFileConversionService.cs` 中重复
- 可配置性降低

## 修复优先级

| 优先级 | 后端 | 前端 |
|--------|------|------|
| P1 | 拆分 `OpenSearchIndexService`、`MinerUFileParseWorker`、`DocumentFileEndpoints` | 修复 StatusStrip 过滤、替换 `document.write`、转义 ChartLine labels |
| P2 | 中文注释英文化、减少 `!` 使用、拆分大测试文件 | 收敛重复工具函数、拆分 `app.scss`、拆分大视图、提取 preview utils |
| P3 | 拆分 `DocumentAnalysisService` / `MinerUPrecisionClient` 类型 | 拆分 `docApi.ts`、NIT 清理 |

## 验证命令

```bash
# 后端
dotnet build src/Doctheca.sln
dotnet test src/Doctheca.sln

# 前端
cd frontend
npm run build
```

## 详细报告

- 后端详细清单：`backend-audit-2026-07.md`
- 前端详细清单：`frontend-audit-2026-07.md`
