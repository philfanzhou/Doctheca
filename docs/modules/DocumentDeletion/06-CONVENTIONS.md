# DocumentDeletion — 规范约定 (CONVENTIONS)

## 1. 命名约定

### 1.1 类型命名

| 类别 | 规则 | 示例 |
|------|------|------|
| 领域服务方法 | PascalCase + Async 后缀 | `DeleteDocumentAsync`, `GetDocumentByTitleAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `DeleteByDocumentIdAsync`, `GetByTitleAsync` |
| Admin 端点方法 | PascalCase，与路由动作一致 | `DeleteDocument` |
| 测试类 | 被测功能名 + `Tests` | `DocumentDeletionTests` |
| 测试方法 | `方法名_场景_预期行为` | `DeleteDocumentAsync_SearchIndexFails_StillReturnsTrue` |

### 1.2 字段 / 属性命名

- C# 属性：**PascalCase** — `Title`, `FilePath`, `Deleted`
- JSON 响应字段：**snake_case** — `title`, `deleted`, `file_path`
- 私有字段：**camelCase 前加下划线** — `_documentRepository`, `_searchIndexService`, `_qdrantService`

### 1.3 路由约定

- Admin 端点前缀：`/admin/documents/`
- 删除端点：`DELETE /admin/documents/{title}`

---

## 2. 日志和安全要求

### 2.1 日志级别

| 级别 | 使用场景 |
|------|----------|
| `Information` | 正常删除操作完成 |
| `Warning` | OSS 文件删除失败（非致命） |
| `Error` | 搜索索引清理失败~~、向量索引清理失败~~，必须附带 `ex` 参数 |

### 2.2 日志模板

```csharp
// 好
_logger.LogInformation("文档已删除：{Title}", title);
_logger.LogError(ex, "删除文档搜索索引失败：{Title}", title);
_logger.LogError(ex, "删除文档向量数据失败：{Title}", title);
_logger.LogWarning(ex, "删除文档文件失败：{FilePath}", document.FilePath);

// 禁止：使用字符串拼接
_logger.LogError($"Failed: {title}");
```

### 2.3 禁止记录的内容

- OSS 访问密钥 / SecretKey
- 用户原始密码、Token

### 2.4 允许记录的内容

- `title`（文档标题）
- `FilePath`（OSS 对象路径，不含敏感内容）
- `document.Id`（文档 ID）
- 删除结果（成功/失败）
- 索引清理异常信息（Error 级别）

### 2.5 安全要点

- **仅管理员可执行删除**：通过 Admin 端点路由鉴权。
- **操作日志审计**：删除操作应完整记录，便于事后追溯。

---

## 3. 错误消息格式约定

### 3.1 领域服务

| 场景 | 处理方式 | 日志 |
|------|----------|------|
| 文档不存在 | 返回 true | 无错误日志（幂等） |
| 搜索索引清理失败 | try/catch 捕获 | `LogError(ex, "删除文档搜索索引失败：{Title}", title)` |
| 向量索引清理失败 | ~~try/catch 捕获~~ — **已移除**（2026-06-12） | ~~`LogError(ex, "删除文档向量数据失败：{Title}", title)`~~ |
| 数据库操作失败 | 异常冒泡 | 由上层处理 |

### 3.2 Admin 端点

| 场景 | 处理方式 | 日志 / 返回 |
|------|----------|-------------|
| OSS 文件删除失败 | try/catch 捕获 | `LogWarning(ex, "删除文档文件失败：{FilePath}", document.FilePath)` |
| 正常删除 | — | `{ success: true, data: { title, deleted: true/false } }` |

### 3.3 响应格式约定

- `success: true` 时 `data` 包含 `title` 和 `deleted` 字段
- `deleted: true` 表示文档存在并被删除
- `deleted: false` 表示文档不存在（幂等）
- 当前实现中 `success` 始终为 `true`（删除操作不会失败）

---

## 4. 测试工具要求

### 4.1 强制框架

| 用途 | 工具 | 最低版本 |
|------|------|----------|
| 测试框架 | **xUnit** | 2.6+ |
| Mock | **Moq** | 4.20+ |
| 断言 | xunit `Assert` | 内置 |

### 4.2 测试项目命名

- `Ruoyu.Study.DocRetrieval.Tests.Unit` — 单元测试
- `Ruoyu.Study.DocRetrieval.Tests.Integration` — 集成测试

### 4.3 测试分类

- 所有测试类放在 `DocumentDeletion/DocumentDeletion*Tests.cs`
- 通过 `--filter FullyQualifiedName~DocumentDeletion` 一键执行

### 4.4 禁止事项

- 禁止使用真实 OSS 账号跑单元测试；必须 mock `IOssService`。
- 禁止使用真实搜索索引跑单元测试；必须 mock `ISearchIndexService`。
- 禁止使用真实 Qdrant 服务跑单元测试；必须 mock `IQdrantService`。
- 禁止测试依赖特定执行时间或时区。

---

## 5. 代码风格

### 5.1 一般性规则

- 缩进：**4 空格**，不使用 Tab
- 行宽：建议 ≤ 120 字符
- 文件编码：**UTF-8 without BOM**
- `using` 分组：先 System / BCL，再第三方，再本项目内部
- `namespace` 使用文件顶层命名空间（C# 10+）
- `async Task` 方法全部使用后缀 `Async`

### 5.2 可空性

- 启用 `<Nullable>enable</Nullable>`
- `ISearchIndexService` 和 ~~`IQdrantService`~~ 为可选依赖（nullable），通过 `?.` 安全调用。 — 注：`IQdrantService` 已于 2026-06-12 移除安全调用

### 5.3 异步与并发

- 所有 I/O 方法返回 `Task` / `Task<T>`，遵循 `Async` 后缀
- 调用方使用 `await`，**禁止** `.Result` / `.Wait()`

### 5.4 事务与一致性

- 级联删除 + `SaveChangesAsync` 在同一事务内
- 搜索索引和向量索引清理在事务外，失败不回滚数据库
- OSS 文件删除在数据库删除之后，失败不回滚数据库
- 索引清理异常被捕获，不阻塞后续流程

### 5.5 注释

- 不写"做了什么"类的表面注释
- 如需注释，用单行 `// ...` 形式
- 对"索引清理失败不阻塞"这种非直觉行为应加注释说明

---

## 6. 变更审批

- 对 `DeleteDocumentAsync` 方法签名、Admin 端点路由的任何变更，需要：
  1. 更新本文件与 `SPEC.md` / `DESIGN.md`
  2. 新增或调整对应的单元/集成测试，确保通过
- 级联删除顺序变更为重要一致性变更，需单独评审。
