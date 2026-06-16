# DocumentUpload — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.docretrieval/
├── src/
│   ├── Service/
│   │   └── DocumentAdminEndpoints.cs              # 上传端点 (HTTP 层)
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentModel.cs                   # 文档领域模型
│   │   │   ├── DocumentIngestionJobModel.cs       # 导入任务领域模型
│   │   │   └── DocRetrievalConstants.cs           # 常量定义
│   │   ├── Exceptions/
│   │   │   └── DocRetrievalValidationException.cs # 验证异常
│   │   ├── Services/
│   │   │   └── DocumentDomainService.cs           # 领域服务 (核心逻辑)
│   │   └── Repositories/
│   │       ├── IDocumentRepository.cs             # 文档仓储接口
│   │       ├── IDocumentIngestionJobRepository.cs # 导入任务仓储接口
│   │       └── IUnitOfWork.cs                     # 事务接口
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentEntity.cs                  # 文档数据库实体
│       │   └── DocumentIngestionJobEntity.cs      # 导入任务数据库实体
│       └── Repositories/
│           └── DocumentRepository.cs              # 仓储实现 (EF Core + PostgreSQL)
└── docs/modules/DocumentUpload/                   # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

PostgreSQL 表 `documents` 列：`id (uuid, PK)`, `title (varchar 200)`, `source_type (varchar 20)`, `file_hash (varchar 64)`, `file_path (varchar 500)`, `file_size (bigint)`, `language (varchar 10)`, `grade (varchar 20)`, `subject (varchar 20)`, `year (varchar 10)`, `tags (text, nullable)`, `status (varchar 20)`, `created_at (timestamptz)`, `updated_at (timestamptz, nullable, ConcurrencyCheck)`。

PostgreSQL 表 `document_ingestion_jobs` 列：`id (uuid, PK)`, `document_id (uuid, FK)`, `status (varchar 20)`, `parser_version (varchar 20, nullable)`, `ocr_version (varchar 20, nullable)`, `error_message (text, nullable)`, `started_at (timestamptz, nullable)`, `finished_at (timestamptz, nullable)`, `created_at (timestamptz)`。

## 关键接口签名和数据结构定义

### 领域模型

```csharp
// src/Domain/Models/DocumentModels.cs
public class DocumentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Language { get; set; } = "en";
    public string Grade { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class DocumentIngestionJobModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Status { get; set; } = "pending";
    public string? ParserVersion { get; set; }
    public string? OcrVersion { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 常量定义

```csharp
// src/Domain/Models/Constants.cs
public static class DocRetrievalConstants
{
    public const string SubjectEnglish = "英语";
    public static readonly string[] ValidSubjects = [SubjectEnglish];
    public static readonly string[] ValidGrades =
    [
        "K", "G1", "G2", "G3", "G4", "G5", "G6",
        "G7", "G8", "G9", "G10", "G11", "G12"
    ];
    public static bool IsValidSubject(string subject) => ValidSubjects.Contains(subject);
    public static bool IsValidGrade(string grade) => ValidGrades.Contains(grade);
}
```

### 验证异常

```csharp
// src/Domain/Exceptions/DocRetrievalValidationException.cs
public class DocRetrievalValidationException : Exception
{
    public DocRetrievalValidationException(string message) : base(message) { }
}
```

### 端点签名

```csharp
// src/Service/DocumentAdminEndpoints.cs
// ★ /admin/documents/ 端点组通过 .RequireAuthorization() 要求 JWT Bearer 认证（Identity 签发，JWKS 验证）
POST /admin/documents/upload (multipart/form-data: file, title, subject, grade, year, tags)

// 内部逻辑流程：
// 1. 校验 form content type
// 2. 校验 file 存在性
// 3. 校验 file 大小 (≤ 200MB)
// 4. 校验 file MIME 类型
// 5. 提取元数据字段
// 6. 校验元数据必填性
// 7. 加密 PDF 检测 (IsEncryptedPdf)
// 8. 计算 SHA-256 哈希 → stream.Position=0
// 9. 上传 OSS (docretrieval/{Guid}{ext})
// 10. 推导 sourceType
// 11. 构建 DocumentModel
// 12. 调用 DocumentDomainService.CreateDocumentAsync
// 13. 捕获 DocRetrievalValidationException → 映射错误码
// 14. 返回结果
```

### 领域服务方法签名

```csharp
// src/Domain/Services/DocumentDomainService.cs
// 创建文档 + 导入任务（含元数据校验、标题去重、哈希去重、原子写入）
public async Task<DocumentModel> CreateDocumentAsync(DocumentModel document);

// 内部流程：
// ValidateDocumentMetadata → GetByTitleAsync → GetByFileHashAndStatusAsync(hash, "ready")
// → AddAsync(document) → AddAsync(job) → SaveChangesAsync
```

### 仓储接口

```csharp
// src/Domain/Repositories/IDocumentRepository.cs
public interface IDocumentRepository
{
    Task AddAsync(DocumentModel model);
    Task<DocumentModel?> GetByIdAsync(Guid id);
    Task<DocumentModel?> GetByTitleAsync(string title);
    Task<DocumentModel?> GetByFileHashAsync(string fileHash);
    Task<DocumentModel?> GetByFileHashAndStatusAsync(string fileHash, string status);
    Task<bool> UpdateAsync(DocumentModel model);
    Task<bool> DeleteAsync(Guid id);
    Task<(List<DocumentModel> Items, int TotalCount)> GetListAsync(
        int page, int size, string? status = null, string? subject = null,
        string? grade = null, string? keyword = null, string? year = null);
}

public interface IDocumentIngestionJobRepository
{
    Task AddAsync(DocumentIngestionJobModel model);
    Task<DocumentIngestionJobModel?> GetByIdAsync(Guid id);
    Task<DocumentIngestionJobModel?> GetByDocumentIdAsync(Guid documentId);
    Task<List<DocumentIngestionJobModel>> GetByStatusAsync(string status);
    Task<bool> UpdateAsync(DocumentIngestionJobModel model);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
```

### 注入的外部依赖

```csharp
// DocumentAdminEndpoints.UploadDocument 方法参数
// HttpRequest request
// DocumentDomainService documentService
// IOssService ossService
// ILogger logger

// DocumentDomainService 构造函数签名
public DocumentDomainService(
    IDocumentRepository documentRepository,
    IDocumentPageRepository pageRepository,
    IDocumentSegmentRepository segmentRepository,
    IQuestionSegmentRepository questionRepository,
    IDocumentOccurrenceRepository occurrenceRepository,
    IDocumentIngestionJobRepository jobRepository,
    IUnitOfWork unitOfWork,
    ILogger<DocumentDomainService> logger,
    ISearchIndexService? searchIndexService = null)
```

- `IDocumentRepository`：文档表仓储，提供 `GetByTitleAsync`、`GetByFileHashAndStatusAsync`、`AddAsync`
- `IDocumentIngestionJobRepository`：导入任务仓储，提供 `AddAsync`
- `IUnitOfWork`：事务控制，提供 `SaveChangesAsync`
- `IOssService`：OSS 上传，提供 `UploadAsync`
- `ILogger`：日志记录
- `ISearchIndexService`：搜索索引（上传流程不使用，其他方法使用）

## 数据流描述（步骤序列）

### 上传流程 (`UploadDocument`)

```
客户端
  │
  │ POST /admin/documents/upload (multipart/form-data)
  ▼
DocumentAdminEndpoints.UploadDocument
  │
  ├─ 1. 校验 HasFormContentType → 否则 400
  │
  ├─ 2. 提取 file 字段
  │     ├─ file == null || file.Length == 0 → 400 (DOCRETRIEVAL_FILE_REQUIRED)
  │     ├─ file.Length > 200MB → 400
  │     └─ file.ContentType ∉ AllowedMimeTypes → 400 (DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED)
  │
  ├─ 3. 提取 title/subject/grade/year/tags
  │     └─ 任一必填项为空 → 400 (DOCRETRIEVAL_METADATA_REQUIRED)
  │
  ├─ 4. 打开文件流
  │     ├─ IsEncryptedPdf(stream, contentType) → 400 (DOCRETRIEVAL_FILE_ENCRYPTED)
  │     ├─ SHA256.ComputeHashAsync(stream) → fileHash
  │     ├─ stream.Position = 0
  │     └─ ossService.UploadAsync(stream, "docretrieval/{Guid}{ext}", contentType, Uploads) → filePath
  │
  ├─ 5. 推导 sourceType (pdf/word/ppt)
  │
  ├─ 6. 构建 DocumentModel
  │
  ├─ 7. 调用 DocumentDomainService.CreateDocumentAsync(document)
  │     │
  │     ├─ ValidateDocumentMetadata(document)
  │     │     ├─ Title 为空或 >200 字符 → 抛异常
  │     │     ├─ Subject 不在 ValidSubjects → 抛异常
  │     │     ├─ Grade 不在 ValidGrades → 抛异常
  │     │     ├─ Year 为空 → 抛异常
  │     │     └─ FileHash 为空 → 抛异常
  │     │
  │     ├─ GetByTitleAsync(title) → 非空 → 抛异常 ("Document title already exists")
    │     │
    │     ├─ GetByFileHashAndStatusAsync(hash, "ready") → 非空 → 抛异常 ("File already imported")
  │     │
  │     ├─ 设置 Id/Status/CreatedAt
  │     ├─ documentRepository.AddAsync(document)
  │     ├─ 构建 DocumentIngestionJobModel (Status=DocumentStatus.Pending)
  │     ├─ jobRepository.AddAsync(job)
  │     └─ unitOfWork.SaveChangesAsync() ← 原子提交
  │
  ├─ 8. 捕获 DocRetrievalValidationException → 映射错误码与 HTTP 状态码
  │
  └─ 9. 返回 200 { success, data: { documentId, title, jobId, status } }
```

### 文档状态流转

```
pending ──IngestionWorker消费──▶ processing ──成功──▶ ready
   │                               │
   │                               │ 失败
   │                               ▼
   │                            failed
   │
   └──管理员删除──▶ (已删除)
                       ↑
                       CancelIngestionJobAsync: 标记 job 为 cancelled，
                       同时更新 document.Status = cancelled（与 FailIngestionJobAsync 一致）
```

## 错误处理策略

| 错误场景 | 处理层 | 异常/返回 | HTTP 状态码 | 错误码 |
| --- | --- | --- | --- | --- |
| 未认证请求 | ASP.NET Core 中间件 | 自动拦截 | 401 | — |
| 非 multipart/form-data | 端点 | `Results.BadRequest` | 400 | — |
| 文件为空 | 端点 | `Results.BadRequest` | 400 | `DOCRETRIEVAL_FILE_REQUIRED` |
| 文件大小超限 | 端点 | `Results.BadRequest` | 400 | — |
| 文件格式不支持 | 端点 | `Results.BadRequest` | 400 | `DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED` |
| 元数据缺失 | 端点 | `Results.BadRequest` | 400 | `DOCRETRIEVAL_METADATA_REQUIRED` |
| 加密 PDF | 端点 | `Results.BadRequest` | 400 | `DOCRETRIEVAL_FILE_ENCRYPTED` |
| 标题已存在 | 领域服务 | `DocRetrievalValidationException` | 409 | `DOCRETRIEVAL_TITLE_ALREADY_EXISTS` |
| 文件哈希重复(ready) | 领域服务 | `DocRetrievalValidationException` | 409 | `DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS` |
| 学科非法 | 领域服务 | `DocRetrievalValidationException` | 400 | `DOCRETRIEVAL_SUBJECT_INVALID` |
| 年级非法 | 领域服务 | `DocRetrievalValidationException` | 400 | `DOCRETRIEVAL_GRADE_INVALID` |
| 元数据校验失败(领域层) | 领域服务 | `DocRetrievalValidationException` | 400 | `DOCRETRIEVAL_METADATA_REQUIRED` |
| OSS 上传失败 | 端点 | 异常向上抛出 | 500 | — |
| 数据库写入失败 | 领域服务 | 异常向上抛出 | 500 | — |

**策略要点**：
- **端点层前置校验**：文件存在性、大小、格式、元数据必填性、加密检测在端点层完成，避免无效数据进入领域服务。
- **领域层业务校验**：学科/年级合法性、标题唯一性、文件哈希去重在领域服务层完成，通过 `DocRetrievalValidationException` 统一抛出。
- **端点层异常映射**：`UploadDocument` 捕获 `DocRetrievalValidationException`，根据消息内容映射到对应的 HTTP 状态码和错误码。
- **原子写入**：文档记录和导入任务在同一 `SaveChangesAsync` 中提交，避免部分写入。
- **OSS 先行**：OSS 上传在领域服务调用之前完成，若领域服务校验失败，OSS 文件已上传但无关联记录（可由定期清理任务处理孤立文件）。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IDocumentRepository` | 文档 CRUD、按标题查询、按哈希+状态查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentIngestionJobRepository` | 导入任务 CRUD、按文档ID/状态查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IOssService` | `UploadAsync` 上传文件到 OSS | `Ruoyu.Study.Common.Oss` |
| `ILogger` | 日志记录 | `Microsoft.Extensions.Logging` |
| `DocRetrievalConstants` | 学科/年级合法性校验 | `Ruoyu.Study.DocRetrieval.Domain.Models` |

## 可测试性设计

- **依赖注入接口化**：`IDocumentRepository`、`IDocumentIngestionJobRepository`、`IUnitOfWork`、`IOssService` 全部通过构造函数/方法参数注入，可被 Moq 替换。
- **纯函数计算**：`IsEncryptedPdf` 为静态方法，可独立测试；`SHA256.ComputeHashAsync` 为标准库方法，行为确定。
- **验证异常可区分**：`DocRetrievalValidationException` 的 `Message` 包含具体错误信息，端点层通过消息内容映射错误码，测试可断言消息文本。
- **常量可扩展**：`DocRetrievalConstants.ValidSubjects` 和 `ValidGrades` 为 `readonly` 数组，校验方法 `IsValidSubject`/`IsValidGrade` 可独立测试。
- **失败路径可触发**：Moq 的 `ThrowsAsync` 可模拟 OSS/数据库失败，验证异常传播。
