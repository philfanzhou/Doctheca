# DocumentDeletion — 规格与功能要求 (SPEC)

## 1. 功能概述

DocumentDeletion 模块负责删除指定文档及其全部关联数据。其核心职责是：**按标题删除文档，级联清理数据库记录、搜索索引、向量索引和 OSS 文件，保证数据一致性，且操作幂等**。

模块提供 1 类能力：

1. 删除文档及级联数据（`DeleteDocument`）

相关用户故事见 [FEATURE.md](./01-FEATURE.md)。

---

## 2. 功能要求清单

### 2.1 文档删除

- [ ] **REQ-DEL-01**：给定有效的 `title`，删除对应的文档及全部关联数据（occurrences、questions、segments、pages）。
- [ ] **REQ-DEL-02**：级联删除顺序为：occurrences → questions → segments → pages → document → SaveChanges。
- [ ] **REQ-DEL-03**：文档不存在时返回 true（幂等）。
- [ ] **REQ-DEL-04**：搜索索引清理在 SaveChanges 之后执行，失败仅记 Error 日志，不影响返回结果。
- [ ] **REQ-DEL-05**：向量索引清理在 SaveChanges 之后执行，失败仅记 Error 日志，不影响返回结果。
- [ ] **REQ-DEL-06**：Admin 端点先删数据库再删 OSS 文件，OSS 删除失败仅记 Warning 日志。
- [ ] **REQ-DEL-07**：Admin 端点返回 `{ success: true, data: { title, deleted: document != null } }`。
- [ ] **REQ-DEL-08**：仅管理员可调用此接口。

---

## 3. 详细验收标准（可自动验证）

### 3.1 正常删除

**场景 A：删除存在的文档**
- 给定 `title = "英语试卷2024"`，文档存在且有关联数据
- 当调用 `DeleteDocumentAsync`
- 则 `_occurrenceRepository.DeleteByDocumentIdAsync` 被调用
- 且 `_questionRepository.DeleteByDocumentIdAsync` 被调用
- 且 `_segmentRepository.DeleteByDocumentIdAsync` 被调用
- 且 `_pageRepository.DeleteByDocumentIdAsync` 被调用
- 且 `_documentRepository.DeleteAsync` 被调用
- 且 `_unitOfWork.SaveChangesAsync()` 被调用
- 且 `_searchIndexService.DeleteDocumentIndexAsync` 被调用
- 且 `_qdrantService.DeleteDocumentVectorsAsync` 被调用
- 且返回 `true`

**场景 B：删除不存在的文档（幂等）**
- 给定 `title = "不存在的文档"`，文档不存在
- 当调用 `DeleteDocumentAsync`
- 则无级联删除操作被调用
- 且返回 `true`

### 3.2 Admin 端点

**场景 C：删除存在的文档（含 OSS 文件）**
- 给定 `title = "英语试卷2024"`，文档存在且 `FilePath` 非空
- 当调用 `DELETE /admin/documents/{title}`
- 则先调用 `GetDocumentByTitleAsync` 获取文档信息
- 且调用 `DeleteDocumentAsync` 删除数据库记录
- 且调用 `ossService.DeleteAsync(document.FilePath)` 删除 OSS 文件
- 且返回 `{ success: true, data: { title: "英语试卷2024", deleted: true } }`

**场景 D：删除文档但无 OSS 文件**
- 给定文档存在但 `FilePath` 为空
- 当调用 `DELETE /admin/documents/{title}`
- 则不调用 `ossService.DeleteAsync`
- 且返回 `{ success: true, data: { title: "...", deleted: true } }`

### 3.3 索引清理容错

**场景 E：搜索索引清理失败**
- 给定 `_searchIndexService.DeleteDocumentIndexAsync` 抛出异常
- 当调用 `DeleteDocumentAsync`
- 则异常被捕获，仅记 Error 日志
- 且返回 `true`

**场景 F：向量索引清理失败**
- 给定 `_qdrantService.DeleteDocumentVectorsAsync` 抛出异常
- 当调用 `DeleteDocumentAsync`
- 则异常被捕获，仅记 Error 日志
- 且返回 `true`

### 3.4 OSS 清理容错

**场景 G：OSS 删除失败**
- 给定 `ossService.DeleteAsync` 抛出异常
- 当调用 `DELETE /admin/documents/{title}`
- 则异常被捕获，仅记 Warning 日志
- 且返回 `{ success: true, data: { title: "...", deleted: true } }`

---

## 4. 非功能需求

### 4.1 性能

| 指标 | 目标 | 备注 |
|------|------|------|
| 删除操作响应时间 | P95 < 2s | 依赖数据库、搜索索引、向量索引、OSS 操作 |
| 级联删除关联数据 | P95 < 500ms | 依赖数据库批量删除 |

### 4.2 安全

- **仅管理员可执行**：通过 Admin 端点路由鉴权。
- **操作日志审计**：删除操作应完整记录文档标题和结果。

### 4.3 可靠性 / 一致性

- **先落库、再清索引**：数据库级联删除在同一 `SaveChangesAsync` 事务内，索引清理在事务外。
- **索引清理容错**：搜索索引和向量索引清理失败仅记日志，不影响返回结果。
- **OSS 清理容错**：OSS 文件删除失败仅记 Warning 日志，不影响接口返回。
- **幂等性**：文档不存在时返回 true，不报错。

### 4.4 可观测性

- 监控搜索索引清理失败率。
- 监控向量索引清理失败率。
- 监控 OSS 删除失败率。
- 索引清理失败率 > 1% 时告警。

---

## 5. 测试策略

### 5.1 覆盖率目标

- 对 `DeleteDocumentAsync` 和 `DeleteDocument` 端点的所有分支 **100% 覆盖**。

### 5.2 必须测试的错误路径

1. 文档不存在时返回 true（幂等）。
2. 搜索索引清理抛出异常时，返回仍为 true。
3. 向量索引清理抛出异常时，返回仍为 true。
4. OSS 删除抛出异常时，端点仍返回成功。
5. 文档存在但 `FilePath` 为空时，不调用 OSS 删除。

### 5.3 测试环境要求

- **单元测试**：使用 `xUnit` + `Moq`，通过 Mock 替换所有外部依赖。

### 5.4 禁止事项

- 禁止使用真实 OSS 账号跑单元测试。
- 禁止使用真实搜索索引跑单元测试。
- 禁止使用真实 Qdrant 服务跑单元测试。
- 禁止测试依赖特定执行时间。
