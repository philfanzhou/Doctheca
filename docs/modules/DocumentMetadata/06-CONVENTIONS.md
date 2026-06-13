# 文档元数据更新 — 命名与代码规范（CONVENTIONS）

> 适用于：`DocumentDomainService.cs`（UpdateMetadataAsync 方法）、`DocumentAdminEndpoints.cs`（UpdateMetadata 端点），以及未来扩展到该功能的所有相关文件。

---

## 1. 命名约定

### 1.1 文件与命名空间

- **命名空间**：
  - 领域服务放在 `Ruoyu.Study.DocRetrieval.Domain.Services` 命名空间下；
  - Admin 端点放在 `Ruoyu.Study.DocRetrieval.Service` 命名空间下；
  - 异常类 `DocRetrievalValidationException` 放在 `Ruoyu.Study.DocRetrieval.Domain.Services` 命名空间下。

### 1.2 类与方法

| 元素 | 约定 | 示例 |
|------|------|------|
| 领域服务方法 | 动宾短语 + `Async` 后缀 | `UpdateMetadataAsync` |
| Repository 方法 | 动宾短语 + `Async` 后缀 | `GetByTitleAsync`、`UpdateAsync` |
| Admin 端点方法 | PascalCase，描述动作 | `UpdateMetadata` |
| 异常类 | 功能前缀 + 异常类型 | `DocRetrievalValidationException` |

### 1.3 字段与局部变量

- **私有字段**：`_` 前缀 + `camelCase`。示例：`_documentRepository`、`_searchIndexService`、`_logger`。
- **局部变量**：`camelCase`。示例：`document`、`title`、`subject`。
- **可选依赖字段**：使用 `?` 标注可空。示例：`ISearchIndexService? _searchIndexService`、`IQdrantService? _qdrantService`。

### 1.4 HTTP 端点命名

- 路由：`PUT /admin/documents/{title}/metadata`
- 请求体字段：`subject`、`grade`、`year`、`tags`（camelCase）
- 响应字段：`snake_case`（与项目现有风格一致）
- 错误码：`DOCRETRIEVAL_` 前缀 + 大写蛇形，例如 `DOCRETRIEVAL_DOCUMENT_NOT_FOUND`

---

## 2. 日志与安全要求

### 2.1 日志消息模板

- **模板变量使用 PascalCase 并以花括号包裹**（符合结构化日志约定）。
- 禁止使用字符串拼接 `$"..."` 嵌入日志消息。

| 位置 | 模板示例 |
|------|----------|
| 元数据更新成功 | `_logger.LogInformation("文档元数据已更新：{Title}", title)` |
| 搜索索引同步失败 | `_logger.LogError(ex, "更新文档搜索索引元数据失败：{Title}", title)` |

### 2.2 敏感信息处理

- 不在日志中记录请求体原始内容（可能包含用户输入的标签等）。
- 仅记录文档标题（Title），标题为非敏感信息。

### 2.3 日志级别

| 级别 | 使用场景 |
|------|----------|
| `Information` | 元数据更新成功 |
| `Error` | 搜索索引同步失败（含异常对象） |
| `Warning` | 不使用 |
| `Critical` | 不使用 |

---

## 3. 错误消息格式约定

### 3.1 异常消息

所有业务校验异常使用 `DocRetrievalValidationException`，消息为中文：

| 场景 | 消息 |
|------|------|
| 文档不存在 | `"文档不存在"` |
| 文档未就绪 | `"文档未就绪，不允许修改元数据"` |
| 学科无效 | `"学科仅支持：英语"` |
| 年级无效 | `"年级取值非法，有效值：K、G1、G2、...、G12"` |

### 3.2 端点错误响应

```json
{
  "success": false,
  "message": "文档不存在",
  "errorCode": "DOCRETRIEVAL_DOCUMENT_NOT_FOUND"
}
```

### 3.3 错误码映射

端点层通过 `ex.Message` 的 `Contains` 匹配映射 HTTP 状态码：

| 消息关键词 | HTTP 状态码 | errorCode |
|-----------|-------------|-----------|
| "不存在" | 404 | DOCRETRIEVAL_DOCUMENT_NOT_FOUND |
| "未就绪" | 422 | DOCRETRIEVAL_DOCUMENT_NOT_READY |
| "学科仅支持" | 400 | DOCRETRIEVAL_SUBJECT_INVALID |
| "年级取值非法" | 400 | DOCRETRIEVAL_GRADE_INVALID |

---

## 4. 测试工具要求

- **测试框架**：xUnit（`[Fact]`）。
- **Mock 库**：Moq（`Mock<IDocumentRepository>`、`Mock<ISearchIndexService>`）。
- **测试方法命名**：`{被测方法}_{场景}_Should{预期结果}`，例如：
  - `UpdateMetadata_DocumentNotFound_ThrowsValidationException`
  - `UpdateMetadata_DocumentNotReady_ThrowsValidationException`
  - `UpdateMetadata_SearchIndexFails_DoesNotThrow`
  - `UpdateMetadata_NullFields_NotModified`

---

## 5. 代码风格

- **缩进**：4 空格，不混用 tab。
- **`async/await`**：所有异步方法使用 `await`。
- **null 判断模式**：使用 `?? throw` 模式替代 `if/null` 检查。
  ```csharp
  var document = await _documentRepository.GetByTitleAsync(title)
      ?? throw new DocRetrievalValidationException("文档不存在");
  ```
- **可选依赖**：`ISearchIndexService?`、`IQdrantService?` 使用 `?.` 安全调用，内部 `try/catch` 包裹。
- **tags 获取**：端点层使用 `JsonElement.GetRawText()` 获取原始 JSON 文本，不使用 `GetString()`。
- **字段更新**：使用 `if (param != null) document.Field = param` 模式，逐字段判断。
- **时间戳**：使用 `DateTimeOffset.UtcNow`，不使用 `DateTime.Now`。

---

## 6. 审批清单（Review Checklist）

提交/合入本功能相关代码前，至少确认：

- [ ] 方法命名遵循"动宾短语 + `Async`"约定
- [ ] 日志使用占位符模板，不使用 `$""` 拼接
- [ ] 异常消息与文档一致（中文，含标点）
- [ ] 错误码映射与文档一致（404/422/400 + errorCode）
- [ ] tags 使用 `GetRawText()` 获取
- [ ] null 参数不修改对应字段
- [ ] 搜索索引同步失败仅记 LogError，不抛出
- [ ] `ISearchIndexService` 为可选依赖（`?`）
- [ ] ~~`IQdrantService` 为可选依赖（`?`）~~ — **已移除**（2026-06-12，Qdrant 已移除）
- [ ] `dotnet build -c Release` 无错误
- [ ] 相关单元测试全部通过
