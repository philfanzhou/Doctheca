# 文档列表查询与筛选 — 技术设计（DESIGN）

---

## 1. 目录与文件结构

```
src/services/ruoyu.docretrieval/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentModel.cs                              # DocumentModel 实体
│   │   │   └── DocRetrievalConstants.cs                      # DocRetrievalConstants（学科/年级校验）
│   │   ├── Repositories/
│   │   │   └── IDocumentRepository.cs                        # IDocumentRepository（含 GetListAsync）
│   │   └── Services/
│   │       └── DocumentDomainService.cs                       # ★ GetDocumentListAsync
│   ├── Database/
│   │   └── Repositories/
│   │       └── DocumentRepository.cs                          # GetListAsync 的 EF Core 实现
│   └── Service/
│       └── DocumentAdminEndpoints.cs                          # ★ ListDocuments 端点
└── docs/modules/DocumentList/                                  # ★ 本文件所在目录
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
    public async Task<(List<DocumentModel> Items, int TotalCount)> GetDocumentListAsync(
        int page, int size,
        string? status = null,
        string? subject = null,
        string? grade = null,
        string? keyword = null,
        string? year = null)
    {
        if (page <= 0) page = 1;
        if (size <= 0) size = 20;
        if (size > 100) size = 100;

        return await _documentRepository.GetListAsync(page, size, status, subject, grade, keyword, year);
    }
}
```

### 2.2 Repository 接口

```csharp
// src/Domain/Repositories/IDocumentRepository.cs
public interface IDocumentRepository
{
    Task<(List<DocumentModel> Items, int TotalCount)> GetListAsync(
        int page, int size,
        string? status = null,
        string? subject = null,
        string? grade = null,
        string? keyword = null,
        string? year = null);
}
```

### 2.3 Admin 端点

> ★ `/admin/documents/` 端点组通过 `.RequireAuthorization()` 要求 JWT Bearer 认证（Identity 签发，JWKS 验证），未认证请求返回 401。

```csharp
// src/Service/DocumentAdminEndpoints.cs
private static async Task<IResult> ListDocuments(
    IDocumentDomainService documentService,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 20,
    [FromQuery] string? status = null,
    [FromQuery] string? subject = null,
    [FromQuery] string? grade = null,
    [FromQuery] string? keyword = null,
    [FromQuery] string? year = null)
```

### 2.4 实体模型（关键字段）

```csharp
public class DocumentModel
{
    public Guid           Id         { get; set; }
    public string         Title      { get; set; } = string.Empty;
    public string         SourceType { get; set; } = string.Empty;
    public string         Subject    { get; set; } = string.Empty;
    public string         Grade      { get; set; } = string.Empty;
    public string         Year       { get; set; } = string.Empty;
    public string?        Tags       { get; set; }       // JSON 数组字符串
    public string         Status     { get; set; } = "pending";
    public DateTimeOffset CreatedAt  { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

### 2.5 GetDocument 端点

```
GET /admin/documents/{id:guid}
```

```csharp
// src/Service/DocumentAdminEndpoints.cs
private static async Task<IResult> GetDocument(
    Guid id,
    IDocumentDomainService documentService)
```

返回完整文档详情，包含 `fileHash`、`fileSize`、`language` 等字段：

```json
{
  "success": true,
  "data": {
    "id": "...",
    "title": "...",
    "sourceType": "...",
    "fileHash": "...",
    "fileSize": 12345,
    "language": "en",
    "subject": "...",
    "grade": "...",
    "year": "...",
    "tags": ["..."],
    "status": "...",
    "createdAt": "...",
    "updatedAt": "..."
  }
}
```

文档不存在时返回 404：`{ success: false, message: "Document not found", errorCode: "DOCRETRIEVAL_DOCUMENT_NOT_FOUND" }`。

---

## 3. 数据流描述（步骤序列）

```
客户端 GET /admin/documents?page=1&pageSize=20&status=ready&subject=英语
  │
  ▼
DocumentAdminEndpoints.ListDocuments
  │
  ├── 解析 query string 参数（page, pageSize, status, subject, grade, keyword, year）
  │
  ├── 调用 documentService.GetDocumentListAsync(page, pageSize, status, subject, grade, keyword, year)
  │     │
  │     ├── page ≤ 0 → page = 1
  │     ├── size ≤ 0 → size = 20
  │     ├── size > 100 → size = 100
  │     │
  │     └── _documentRepository.GetListAsync(page, size, status, subject, grade, keyword, year)
  │           │
  │           ├── 构建 IQueryable<DocumentEntity>，按条件 Where 过滤
  │           ├── CountAsync() → TotalCount
  │           ├── Skip((page-1)*size).Take(size).ToListAsync() → Items
  │           └── 返回 (Items, TotalCount)
  │
  └── 构造 JSON 响应
        {
          success: true,
          data: items.Select(d => { id, title, sourceType, subject, grade, year,
                                    tags (反序列化), status, createdAt, updatedAt }),
          total: totalCount,
          page,
          pageSize,
          totalPages: (totalCount + pageSize - 1) / pageSize
        }
```

### 3.2 GetDocument 数据流

```
客户端 GET /admin/documents/{id:guid}
  │
  ▼
DocumentAdminEndpoints.GetDocument
  │
  ├── 调用 documentService.GetDocumentAsync(id)
  │     └─ _documentRepository.GetByIdAsync(id)
  │           └─ 返回 DocumentModel?（null 表示不存在）
  │
  ├── document == null → 404 NotFound
  │
  └── 构造 JSON 响应
        {
          success: true,
          data: { id, title, sourceType, fileHash, fileSize, language,
                  subject, grade, year, tags (反序列化), status,
                  createdAt, updatedAt }
        }
```

---

## 4. 错误处理策略

| 位置 | 可能异常 | 处理方式 | 日志级别 |
|------|----------|----------|----------|
| ASP.NET Core 中间件 | 未认证请求 | 自动拦截，返回 401 | — |
| `GetDocumentListAsync` | 数据库连接失败等 | 异常向上抛出，由端点层或中间件统一处理 | `LogError` |
| `ListDocuments` 端点 | `DocRetrievalValidationException` | 当前列表查询不抛此异常 | - |
| `ListDocuments` 端点 | 未预期异常 | ASP.NET Core 中间件统一捕获，返回 500 | `LogError` |

> **设计权衡**：列表查询为纯读操作，不涉及业务校验异常。分页参数修正逻辑在领域层完成，确保无论调用方是 Admin 端点还是未来其他调用方，均能获得一致的修正行为。

---

## 5. 依赖的外部模块接口

| 模块 | 接口 | 用途 |
|------|------|------|
| Domain 层 | `IDocumentRepository.GetListAsync` | 按条件分页查询文档 |
| Database 层 | `DocumentRepository` | EF Core 实现，构建动态 Where 条件 |
| ASP.NET Core | `Results.Ok()` | 构造 200 响应 |
| System.Text.Json | `JsonSerializer.Deserialize<string[]>` | tags 字段从 JSON 字符串反序列化为数组 |

---

## 6. 可测试性设计

1. **领域层分页修正逻辑可独立测试** — `GetDocumentListAsync` 中的参数修正逻辑可通过传入边界值（page=0, size=-1, size=200）验证。
2. **Repository 接口可 Mock** — 测试中 Mock `IDocumentRepository.GetListAsync`，无需真实数据库。
3. **端点返回结构可集成测试** — 使用 `WebApplicationFactory` + InMemory DB 验证 JSON 结构。
4. **筛选条件透传** — 领域层不做筛选逻辑，仅透传给 Repository，筛选逻辑在 Repository 的 EF Core 实现中完成。
