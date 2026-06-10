# DocumentUpload — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.DocRetrieval.Domain.*`；数据库层使用 `Ruoyu.Study.DocRetrieval.Database.*`；服务层使用 `Ruoyu.Study.DocRetrieval.Service`；公共 OSS 组件使用 `Ruoyu.Study.Common.Oss`。
- **类名**：领域服务采用 `XxxDomainService`；模型采用 `XxxModel`；仓储接口采用 `IXxxRepository`；仓储实现采用 `XxxRepository`；实体采用 `XxxEntity`；异常采用 `XxxException`；常量采用 `XxxConstants`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `CreateDocumentAsync`、`GetByTitleAsync`、`GetByFileHashAndStatusAsync`、`ValidateDocumentMetadata`。
- **异常类**：业务校验异常采用 `DocRetrievalValidationException`，消息文本为中文。
- **私有方法**：采用 PascalCase；纯函数（如 `IsEncryptedPdf`）使用 `static`。
- **字段**：私有依赖注入字段采用 `_camelCase` 下划线前缀（如 `_documentRepository`、`_jobRepository`、`_unitOfWork`、`_logger`）。
- **数据库**：表名 `documents`、`document_ingestion_jobs` 小写蛇形；列名 `file_hash`、`file_path`、`source_type`、`created_at`、`updated_at` 蛇形命名；并发字段 `updated_at` 使用 `[ConcurrencyCheck]`。
- **OSS 路径**：`docretrieval/{Guid}{ext}`，Guid 保证唯一性，ext 保留原始扩展名。
- **错误码**：采用 `DOCRETRIEVAL_` 前缀 + 大写蛇形，如 `DOCRETRIEVAL_FILE_REQUIRED`、`DOCRETRIEVAL_TITLE_ALREADY_EXISTS`。

## 日志级别

| 级别 | 使用场景 | 示例 |
| --- | --- | --- |
| `Information` | 关键业务操作成功 | 文档创建成功：`"文档已创建：{Title}，导入任务已排队"` |
| `Warning` | 非致命性异常、可恢复的失败 | OSS 删除失败、搜索索引更新失败 |
| `Error` | 严重错误、不可恢复的失败 | 导入任务失败：`"文档导入失败：{DocumentId}，原因：{Error}"`；搜索索引删除失败 |

**日志安全要求**：
- 禁止在日志中记录文件完整哈希值或 OSS 访问签名。
- 日志模板使用结构化参数（如 `_logger.LogInformation("文档已创建：{Title}，导入任务已排队", document.Title)`）。
- 不得在日志中记录文件内容字节。

## 错误消息格式

### 领域服务异常消息（中文）

| 场景 | 消息文本 | 对应错误码 |
| --- | --- | --- |
| 文档名为空 | `"文档名不能为空"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 文档名超长 | `"文档名超过200字符"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 学科为空 | `"学科不能为空"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 学科非法 | `"学科仅支持：英语"` | `DOCRETRIEVAL_SUBJECT_INVALID` |
| 年级为空 | `"年级不能为空"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 年级非法 | `"年级取值非法，有效值：K、G1、G2、..."` | `DOCRETRIEVAL_GRADE_INVALID` |
| 年份为空 | `"年份不能为空"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 文件哈希为空 | `"文件哈希不能为空"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 标题重复 | `"文档名已存在"` | `DOCRETRIEVAL_TITLE_ALREADY_EXISTS` |
| 文件哈希重复 | `"该文件已被导入"` | `DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS` |

### 端点层错误消息（中文）

| 场景 | 消息文本 | 错误码 |
| --- | --- | --- |
| 非 multipart/form-data | `"请求必须是 multipart/form-data"` | — |
| 文件为空 | `"文件不能为空"` | `DOCRETRIEVAL_FILE_REQUIRED` |
| 文件大小超限 | `"文件大小超过200MB限制"` | — |
| 文件格式不支持 | `"不支持的文件格式"` | `DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED` |
| 元数据缺失 | `"必填元数据缺失（title/subject/grade/year）"` | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 加密文件 | `"不支持加密文件"` | `DOCRETRIEVAL_FILE_ENCRYPTED` |

### 错误响应格式

```json
{
  "success": false,
  "message": "错误描述文本",
  "errorCode": "DOCRETRIEVAL_XXX"
}
```

> 注：端点层通过 `DocRetrievalValidationException.Message` 的 `Contains` 匹配来映射错误码，因此领域服务异常消息文本必须保持稳定。

## 测试工具

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部接口（OSS、仓储、UnitOfWork、日志）必须通过 Mock 替换，不得在测试中访问真实外部资源。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null/Contains/ThrowsAsync`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `CreateDocumentAsync_WhenTitleExists_ShouldThrow`。
- **数据准备**：在测试类构造函数中创建 Mock 对象和被测服务实例。
- **确定性**：时间字段仅做范围断言（如 `±5s`）；GUID 只断言不等于 `Guid.Empty`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共属性；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- `switch` 表达式可用于简单分支（如 `sourceType` 推导）；复杂条件使用 `if/else` 并辅以注释。
- 公共方法在必要时应提供参数 guard（如 `page<=0` 回退为 1）。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 所有 `IDisposable` 资源（如 `Stream`、`SHA256`）必须在 `using` 语句中使用。
- 静态辅助方法（如 `IsEncryptedPdf`）应确保流的 `Position` 在 `finally` 块中恢复。
- 常量定义集中在 `DocRetrievalConstants` 类中，使用 `static readonly` 数组和 `static` 校验方法。
- 允许的 MIME 类型列表和文件大小限制定义为类级别的 `static readonly` 字段。

## 代码评审检查清单

- [ ] 方法命名动词准确、语义清晰、无缩写。
- [ ] 外部依赖均通过接口注入，可在测试中替换。
- [ ] 元数据校验在领域服务层完整覆盖（Title/Subject/Grade/Year/FileHash）。
- [ ] 标题去重和哈希去重逻辑正确（哈希去重仅检查 `ready` 状态）。
- [ ] 文档记录和导入任务在同一 `SaveChangesAsync` 中原子提交，无部分写入。
- [ ] `IsEncryptedPdf` 在 `finally` 块中恢复 `stream.Position`。
- [ ] SHA-256 计算后 `stream.Position = 0` 再上传 OSS。
- [ ] OSS 路径格式为 `docretrieval/{Guid}{ext}`。
- [ ] 错误码映射与 SPEC 错误码表一致。
- [ ] 日志未泄漏敏感信息；日志模板使用结构化参数。
- [ ] 测试命名遵循 `[方法]_[场景]_[预期]` 格式，场景覆盖成功、失败、边界、异常。
- [ ] 无编译警告；`dotnet build --configuration Release` 通过。
