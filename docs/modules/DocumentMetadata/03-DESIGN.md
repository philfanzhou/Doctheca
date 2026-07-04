# 文档元数据更新 — 技术设计（DESIGN）

---

## 1. 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentModel.cs                              # DocumentModel 实体
│   │   │   └── DocLibraryConstants.cs                      # DocLibraryConstants（学科/年级校验）
│   │   ├── Exceptions/
│   │   │   └── DocLibraryValidationException.cs            # 验证异常
│   │   ├── Repositories/
│   │   │   ├── IDocumentRepository.cs                        # IDocumentRepository（含 GetByTitleAsync/UpdateAsync）
│   │   │   ├── IUnitOfWork.cs                                # 事务接口
│   │   │   └── ISearchIndexService.cs                        # ISearchIndexService（含 UpdateDocumentMetadataAsync）
│   │   └── Services/
│   │       └── DocumentDomainService.cs                       # ★ UpdateMetadataAsync
│   ├── Database/
│   │   └── Repositories/
│   │       └── DocumentRepository.cs                          # GetByTitleAsync / UpdateAsync 实现
│   └── Service/
│       └── DocumentAdminEndpoints.cs                          # ★ UpdateMetadata 端点
└── docs/modules/DocumentMetadata/                              # ★ 本文件所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md                                            # 本文件
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

---

## 2. 关键接口签名与数据结构

### 2.1 领域服务方法

```csharp
// src/Domain/Services/DocumentDomainService.cs
public class DocumentDomainService
{
    public async Task<DocumentModel> UpdateMetadataAsync(
        string title, string? subject, string? grade, string? year, string? tags)
    {
        var document = await _documentRepository.GetByTitleAsync(title)
            ?? throw new DocLibraryValidationException("文档不存在");

        if (document.Status != "ready")
            throw new DocLibraryValidationException("文档未就绪，不允许修改元数据");

        if (subject != null && !DocLibraryConstants.IsValidSubject(subject))
            throw new DocLibraryValidationException("学科仅支持：英语");

        if (grade != null && !DocLibraryConstants.IsValidGrade(grade))
            throw new DocLibraryValidationException(
                $"年级取值非法，有效值：{string.Join("、", DocLibraryConstants.ValidGrades)}");

        if (subject != null) document.Subject = subject;
        if (grade != null) document.Grade = grade;
        if (year != null) document.Year = year;
        if (tags != null) document.Tags = tags;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await _documentRepository.UpdateAsync(document);
        await _unitOfWork.SaveChangesAsync();

        // 同步更新搜索索引中的元数据
        if (_searchIndexService != null)
        {
            try
            {
                await _searchIndexService.UpdateDocumentMetadataAsync(
                    document.Id, document.Subject, document.Grade, document.Year);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新文档搜索索引元数据失败：{Title}", title);
            }
        }

        _logger.LogInformation("文档元数据已更新：{Title}", title);
        return document;
    }
}
```

### 2.2 异常类

```csharp
public class DocLibraryValidationException : Exception
{
    public DocLibraryValidationException(string message) : base(message) { }
}
```

### 2.3 搜索索引接口

```csharp
// src/Domain/Repositories/ISearchIndexService.cs
public interface ISearchIndexService
{
    Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year);
    // ... 其他方法
}
```

### 2.4 Admin 端点

> ★ `/admin/documents/` 端点组无应用层认证（内网管理后台，访问控制由部署层网络隔离实现）。

```csharp
// src/Service/DocumentAdminEndpoints.cs
private static async Task<IResult> UpdateMetadata(
    string title,
    HttpRequest request,
    DocumentDomainService documentService)
{
    // 读取 request body，检查大小限制 (≤ 10KB)
    // 超过 10KB → 返回 413 Payload Too Large
    // 解析 JSON body
    // subject/grade/year/tags 四个字段
    // tags 使用 GetRawText() 获取
    // 全部为 null → 400 "至少提供一项元数据"
    // 调用 documentService.UpdateMetadataAsync
    // 异常映射：不存在→404, 未就绪→422, 学科无效→400, 年级无效→400
}
```

### 2.5 常量类

```csharp
// src/Domain/Models/DocLibraryConstants.cs
public static class DocLibraryConstants
{
    public const string SubjectEnglish = "英语";
    public static readonly string[] ValidSubjects = [SubjectEnglish];
    public static readonly string[] ValidGrades = ["K", "G1", "G2", ..., "G12"];
    public static bool IsValidSubject(string subject) => ValidSubjects.Contains(subject);
    public static bool IsValidGrade(string grade) => ValidGrades.Contains(grade);
}
```

---

## 3. 数据流描述（步骤序列）

```
客户端 PUT /admin/documents/{title}/metadata
  │
  ▼
DocumentAdminEndpoints.UpdateMetadata
  │
  ├── 读取 request body
  │     └── body 长度 > 10KB → 返回 413 Payload Too Large
  │
  ├── 解析 body 为 JsonElement
  │
  ├── 提取字段：
  │     subject → GetString()
  │     grade   → GetString()
  │     year    → GetString()
  │     tags    → GetRawText()            ← ★ JSON 数组字符串
  │
  ├── 全部为 null?
  │     └── 是 → 返回 400 "至少提供一项元数据"
  │
  ├── 调用 documentService.UpdateMetadataAsync(title, subject, grade, year, tags)
  │     │
  │     ├── _documentRepository.GetByTitleAsync(title)
  │     │     └── null → throw DocLibraryValidationException("文档不存在")
  │     │
  │     ├── document.Status != "ready"?
  │     │     └── 是 → throw DocLibraryValidationException("文档未就绪，不允许修改元数据")
  │     │
  │     ├── subject != null && !IsValidSubject(subject)?
  │     │     └── 是 → throw DocLibraryValidationException("学科仅支持：英语")
  │     │
  │     ├── grade != null && !IsValidGrade(grade)?
  │     │     └── 是 → throw DocLibraryValidationException("年级取值非法...")
  │     │
  │     ├── 仅更新非 null 字段：
  │     │     if (subject != null) document.Subject = subject;
  │     │     if (grade != null)   document.Grade = grade;
  │     │     if (year != null)    document.Year = year;
  │     │     if (tags != null)    document.Tags = tags;
  │     │
  │     ├── document.UpdatedAt = DateTimeOffset.UtcNow
  │     │
  │     ├── _documentRepository.UpdateAsync(document)
  │     ├── _unitOfWork.SaveChangesAsync()
  │     │
  │     ├── _searchIndexService?.UpdateDocumentMetadataAsync(...)
  │     │     └── 失败 → catch, LogError, 不抛出
  │     │
  │     └── LogInformation("文档元数据已更新：{Title}")
  │
  └── 返回 200 + 更新后的文档数据
        或 catch DocLibraryValidationException → 映射为对应 HTTP 状态码
```

---

## 4. 错误处理策略

| 位置 | 可能异常 | 处理方式 | 日志级别 |
|------|----------|----------|----------|
| `UpdateMetadata` 端点 | 请求体超过 10KB | 返回 413 Payload Too Large | - |
| `UpdateMetadataAsync` | 文档不存在 | 抛 `DocLibraryValidationException("文档不存在")` | - |
| `UpdateMetadataAsync` | 文档未就绪 | 抛 `DocLibraryValidationException("文档未就绪，不允许修改元数据")` | - |
| `UpdateMetadataAsync` | 学科无效 | 抛 `DocLibraryValidationException("学科仅支持：英语")` | - |
| `UpdateMetadataAsync` | 年级无效 | 抛 `DocLibraryValidationException("年级取值非法...")` | - |
| `UpdateMetadataAsync` | 搜索索引同步失败 | `try/catch` 捕获，仅记 `LogError`，不抛出 | `LogError` |
| `UpdateMetadata` 端点 | `DocLibraryValidationException` | 按消息内容映射为 HTTP 状态码 + errorCode | - |
| `UpdateMetadata` 端点 | JSON 解析失败 | 返回 400 "无效的JSON" | - |
| `UpdateMetadata` 端点 | 无元数据 | 返回 400 "至少提供一项元数据" | - |

### 错误码映射表

| 异常消息关键词 | HTTP 状态码 | errorCode |
|---------------|-------------|-----------|
| "不存在" | 404 | DOCLIBRARY_DOCUMENT_NOT_FOUND |
| "未就绪" | 422 | DOCLIBRARY_DOCUMENT_NOT_READY |
| "学科仅支持" | 400 | DOCLIBRARY_SUBJECT_INVALID |
| "年级取值非法" | 400 | DOCLIBRARY_GRADE_INVALID |

> **设计权衡**：搜索索引同步失败采用"仅记日志、不中断主流程"策略。这意味着可能出现数据库元数据已更新但搜索索引未更新的短暂不一致，但保证了管理员的操作体验不受搜索服务故障影响。不一致可通过搜索索引的全量重建兜底。

---

## 5. 依赖的外部模块接口

| 模块 | 接口 | 用途 |
|------|------|------|
| Domain 层 | `IDocumentRepository.GetByTitleAsync` | 按标题查找文档 |
| Domain 层 | `IDocumentRepository.UpdateAsync` | 更新文档 |
| Domain 层 | `IUnitOfWork.SaveChangesAsync` | 提交数据库变更 |
| Domain 层 | `ISearchIndexService.UpdateDocumentMetadataAsync` | 同步搜索索引元数据 |
| Domain 层 | `DocLibraryConstants` | 学科/年级校验 |
| ASP.NET Core | `Results.Json()` | 构造错误响应（带状态码） |
| System.Text.Json | `JsonElement.GetRawText()` | 获取 tags 原始 JSON 文本 |

---

## 6. 可测试性设计

1. **依赖通过构造注入** — `ISearchIndexService` 为可选依赖（`?`），测试时传入 Mock 或 null。
2. **异常类型统一** — 所有业务校验失败均抛 `DocLibraryValidationException`，端点层通过消息内容映射 HTTP 状态码，便于测试。
3. **搜索索引失败隔离** — Mock `ISearchIndexService.UpdateDocumentMetadataAsync` 抛异常，验证主流程不中断。
4. **null 参数行为** — 测试中传入 null 参数，验证对应字段不被修改。
5. **tags 格式验证** — 测试中传入 JSON 数组字符串，验证 `GetRawText()` 获取的值正确存入。
