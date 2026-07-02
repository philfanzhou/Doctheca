# MinerU 文档解析（持久化版）— 设计说明 (DESIGN)

## 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Service/
│   │   ├── DocumentAdminEndpoints.cs              # 端点（文件 + 解析 + 导出）
│   │   ├── MinerUPrecisionClient.cs               # MinerU API 客户端（复用）
│   │   ├── MinerUFileParseWorker.cs               # MinerU 文件解析后台 Worker
│   │   └── ImageMetadata.cs                       # 图片元数据 record
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentFileModel.cs                # 文件领域模型
│   │   │   ├── DocumentParseModel.cs               # 解析领域模型
│   │   │   └── DocumentParseImageModel.cs          # 解析图片领域模型
│   │   ├── Services/
│   │   │   ├── IDocumentFileService.cs             # 文件服务接口
│   │   │   └── IDocumentParseService.cs             # 解析服务接口
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # 文件仓储接口
│   │       ├── IDocumentParseRepository.cs          # 解析仓储接口
│   │       └── IDocumentParseImageRepository.cs     # 解析图片仓储接口
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentFileEntity.cs               # 文件数据库实体
│       │   ├── DocumentParseEntity.cs              # 解析数据库实体
│       │   └── DocumentParseImageEntity.cs          # 解析图片数据库实体
│       ├── Repositories/
│       │   ├── DocumentFileRepository.cs            # 文件仓储实现
│       │   ├── DocumentParseRepository.cs           # 解析仓储实现
│       │   └── DocumentParseImageRepository.cs       # 解析图片仓储实现
│       └── Migrations/
│           └── (Migration)
└── docs/modules/MinerUAgentParsing/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    └── 03-DESIGN.md (本文件)
```

## 数据模型

### DocumentFileEntity

```csharp
[Table("document_files")]
public class DocumentFileEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("file_name")]
    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Column("file_path")]
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Column("content_type")]
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    [ConcurrencyCheck]
    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

### DocumentParseEntity

```csharp
[Table("document_parses")]
public class DocumentParseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")]
    [Required]
    public Guid DocumentFileId { get; set; }

    [Column("status")]
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = DocumentParseStatus.Pending;

    [Column("external_task_id")]
    [MaxLength(100)]
    public string? ExternalTaskId { get; set; }

    [Column("markdown_content")]
    public string? MarkdownContent { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [ForeignKey(nameof(DocumentFileId))]
    public DocumentFileEntity? DocumentFile { get; set; }
}
```

### DocumentParseImageEntity

```csharp
[Table("document_parse_images")]
public class DocumentParseImageEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

    [Column("image_name")]
    [Required]
    [MaxLength(200)]
    public string ImageName { get; set; } = string.Empty;

    [Column("image_path")]
    [Required]
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [Column("content_type")]
    [Required]
    [MaxLength(50)]
    public string ContentType { get; set; } = "image/jpeg";

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }
}
```

### 状态常量

```csharp
public static class DocumentParseStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}
```

## 服务接口

### IDocumentFileService

```csharp
public interface IDocumentFileService
{
    Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size);
    Task<bool> DeleteAsync(Guid id);
}
```

### IDocumentParseService

```csharp
public interface IDocumentParseService
{
    Task<DocumentParseModel> CreateAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null);
    Task<List<DocumentParseModel>> GetPendingJobsAsync();
    Task AddImageAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
    // 新增：解析记录列表（含文档名搜索）
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    // 新增：删除单条解析记录（含 S3 图片清理）
    Task<bool> DeleteParseAsync(Guid parseId, IOssService ossService);
}
```

### IDocumentParseRepository（新增方法）

```csharp
public interface IDocumentParseRepository
{
    Task AddAsync(DocumentParseModel model);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task UpdateAsync(DocumentParseModel model);
    Task<List<DocumentParseModel>> GetByStatusAsync(string status);
    // 新增：分页列表（含搜索）
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    Task DeleteAsync(Guid id);
}
```

### IPdfSplitService

```csharp
public interface IPdfSplitService
{
    int GetPageCount(Stream pdfStream);
    List<(int ChunkIndex, Stream ChunkStream)> SplitPdf(Stream pdfStream, int maxPagesPerChunk = 200);
}
```

## 端点设计

### 文件管理端点组

```csharp
var fileGroup = app.MapGroup("/admin/document-files")
    .RequireAuthorization();

fileGroup.MapPost("/upload", UploadDocumentFile);
fileGroup.MapGet("/", ListDocumentFiles);          // 新增 parseStatus 过滤参数
fileGroup.MapGet("/{id:guid}", GetDocumentFile);
fileGroup.MapPost("/{id:guid}/parse", ParseDocumentFile);
fileGroup.MapDelete("/{id:guid}", DeleteDocumentFile);
fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);
```

### 解析记录端点组（新增）

```csharp
var parseGroup = app.MapGroup("/admin/document-parses")
    .RequireAuthorization();

parseGroup.MapGet("/", ListDocumentParses);                // API-8
parseGroup.MapDelete("/{parseId:guid}", DeleteDocumentParse); // API-9
parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown); // API-10
parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);        // API-11
```

### 文件列表过滤

```
GET /admin/document-files?page=1&pageSize=20&parseStatus=
  │
  ├─ parseStatus 为空：返回所有文件
  ├─ parseStatus=unparsed：返回无 parse 记录的文件
  ├─ parseStatus=pending/parsing/parsed/failed：返回最新 parse 为对应状态的文件
  └─ 实现方式：
       1. 查询所有文件（分页）
       2. 对每个文件查询最新 parse
       3. 在内存中过滤 parseStatus
       4. 重新分页返回
```

### 解析记录列表

```
GET /admin/document-parses?page=1&pageSize=20&search=
  │
  ├─ 1. 查询 document_parses JOIN document_files
  ├─ 2. search 非空时：WHERE file_name ILIKE '%search%'
  ├─ 3. ORDER BY parsed_at DESC NULLS LAST
  └─ 4. 返回 { id, fileName, status, parsedAt, errorMessage }
```

### 删除解析记录

```
DELETE /admin/document-parses/{parseId}
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 查询关联图片 → 从 S3 删除每张图片
  ├─ 3. 删除数据库记录（级联：parse_images → parse）
  └─ 4. 返回 { id, deleted } （原始文件不受影响）
```

### 按解析 ID 导出

```
GET /admin/document-parses/{parseId}/export/markdown
GET /admin/document-parses/{parseId}/export/html
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 校验 status=parsed → 非 parsed 返回 422
  ├─ 3. 查询关联的 document_file 获取文件名
  ├─ 4. 查询关联图片
  └─ 5. 与按文件 ID 导出相同的 ZIP/HTML 构建逻辑
```

## 上传流程

```
POST /admin/document-files/upload
  │
  ├─ 1. 校验 HasFormContentType
  ├─ 2. 提取 file → 校验存在性/大小/格式
  ├─ 3. 上传到 S3 (documents/doclibrary-files/{Guid}{ext})
  ├─ 4. 构建 DocumentFileModel
  ├─ 5. 调用 DocumentFileService.CreateAsync
  └─ 6. 返回 { id, fileName, contentType }
```

## 解析流程

```
POST /admin/document-files/{id}/parse
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 如果 status ∈ {pending, parsing} 返回 422
  ├─ 3. 校验 MinerU Token 配置 → 未配置返回 503
  ├─ 4. 调用 DocumentParseService.CreateAsync(fileId) → 新建 parse 记录
  └─ 5. 返回 { id, parseId, status }
```

## MinerUFileParseWorker 流程

```
MinerUFileParseWorker (BackgroundService, 每 5 秒轮询)
  │
  ├─ 1. 查询 GetPendingJobsAsync()
  │
  ├─ 2. 对每个 pending 任务：
  │     ├─ 更新 status=parsing
  │     ├─ 查询关联的 document_file 获取 S3 路径
  │     ├─ 下载源文件到临时流
  │     │
  │     ├─ [非 PDF 文件]：格式转换
  │     │   ├─ 调用 IFileConversionService.ConvertToPdfAsync
  │     │   ├─ 使用 LibreOffice headless 转换为 PDF
  │     │   └─ 转换失败 → 标记 failed，跳过后续步骤
  │     │
  │     ├─ 检测 PDF 页数
  │     │
  │     ├─ [页数 ≤ 200]：直接提交
  │     │   ├─ 上传 PDF（转换后的或原始的）到 S3
  │     │   ├─ 生成 presigned URL
  │     │   ├─ 提交 MinerU → 轮询 → 下载 ZIP → 处理结果
  │     │   └─ 保存结果
  │     │
  │     └─ [页数 > 200]：拆分解析
  │         ├─ 将 PDF 按每 200 页拆分为 N 个子 PDF
  │         ├─ 上传每个子 PDF 到 S3（临时路径 mineru/splits/{parseId}/chunk_{i}.pdf）
  │         ├─ 逐个提交 MinerU → 轮询 → 下载 ZIP → 处理结果
  │         ├─ 合并所有子文档的 Markdown + 图片
  │         ├─ 删除 S3 上的临时子 PDF 文件
  │         └─ 保存合并结果为一条 parse 记录
  │
  └─ 3. 继续下一个 pending 任务
```

### 格式转换设计（IFileConversionService）

非 PDF 文件（DOCX/PPTX）在解析前先转换为 PDF，统一后续处理流程。

```csharp
public interface IFileConversionService
{
    /// <summary>
    /// Check if LibreOffice is available for conversion.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Convert a non-PDF file stream to PDF.
    /// Returns the converted PDF stream, or null if conversion fails.
    /// </summary>
    Task<Stream?> ConvertToPdfAsync(Stream sourceStream, string fileName, CancellationToken ct = default);
}
```

#### LibreOffice headless 转换实现

```csharp
public class LibreOfficeConversionService : IFileConversionService
{
    // 调用: libreoffice --headless --convert-to pdf --outdir /tmp /tmp/source.docx
    // 1. 将源文件写入临时文件
    // 2. 启动 libreoffice --headless --convert-to pdf
    // 3. 读取转换后的 PDF 文件
    // 4. 清理临时文件
    // 5. 返回 PDF 流
}
```

#### 转换流程

```
非 PDF 文件 → IFileConversionService.ConvertToPdfAsync
  │
  ├─ 1. 检查 IsAvailable → 不可用则抛异常
  ├─ 2. 将源文件写入 /tmp/{guid}/{fileName}
  ├─ 3. 执行: libreoffice --headless --convert-to pdf --outdir /tmp/{guid} /tmp/{guid}/{fileName}
  ├─ 4. 等待进程完成（超时 60 秒）
  ├─ 5. 读取 /tmp/{guid}/{fileName_without_ext}.pdf
  ├─ 6. 清理 /tmp/{guid}/ 目录
  └─ 7. 返回 PDF 流
```

### 大文档拆分详细设计

#### PDF 页数检测

使用 `PdfSharpCore` 或 `iText7` 读取 PDF 页数。仅读取元数据，不加载全部内容。

```csharp
// 使用 PdfSharpCore（轻量、免费）
using PdfSharpCore.Pdf;
using var doc = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Information);
var pageCount = doc.PageCount;
```

#### PDF 拆分

使用 `PdfSharpCore` 将 PDF 按页数拆分：

```csharp
List<Stream> SplitPdf(Stream sourcePdf, int maxPagesPerChunk)
{
    using var doc = PdfReader.Open(sourcePdf, PdfDocumentOpenMode.Import);
    var chunks = new List<Stream>();
    var totalPages = doc.PageCount;
    
    for (int startPage = 0; startPage < totalPages; startPage += maxPagesPerChunk)
    {
        var chunk = new PdfDocument();
        int endPage = Math.Min(startPage + maxPagesPerChunk, totalPages);
        for (int i = startPage; i < endPage; i++)
        {
            chunk.AddPage(doc.Pages[i]);
        }
        var ms = new MemoryStream();
        chunk.Save(ms, false);
        ms.Position = 0;
        chunks.Add(ms);
    }
    return chunks;
}
```

#### Markdown 合并

多个子文档的 Markdown 按顺序拼接，用分隔符标记原始页码范围：

```csharp
string MergeMarkdown(List<string> markdownParts, List<int> startPages)
{
    var sb = new StringBuilder();
    for (int i = 0; i < markdownParts.Count; i++)
    {
        if (i > 0) sb.AppendLine("\n\n---\n\n");  // 分页分隔
        sb.Append(markdownParts[i]);
    }
    return sb.ToString();
}
```

#### 图片合并

所有子文档的图片统一收集，图片名可能重复（不同子文档可能有同名图片如 `images/abc.jpg`）。
解决方案：为每个子文档的图片添加 chunk 前缀避免冲突。

```
子文档 0 的图片: mineru/{taskId}_chunk0/abc.jpg → 重命名为 chunk0_abc.jpg
子文档 1 的图片: mineru/{taskId}_chunk1/abc.jpg → 重命名为 chunk1_abc.jpg
```

#### 临时文件清理

拆分产生的子 PDF 上传到 S3 的临时路径 `mineru/splits/{parseId}/chunk_{i}.pdf`，
在解析完成（无论成功或失败）后删除。

## 图片管理策略

- **S3 路径**：`documents/mineru/{taskId}/{imageName}`（由 `DownloadAndProcessZipAsync` 上传）
- **MD 中的路径**：`DownloadAndProcessZipAsync` 将相对路径替换为 S3 路径
- **查看时**：`GetDocumentFile` 端点将 S3 路径替换为 presigned URL
- **图片元数据**：`DownloadAndProcessZipAsync` 返回 `List<ImageMetadata>`，Worker 直接写入 `document_parse_images` 表
- **Presigned URL 有效期**：1 小时
- **删除文件时**：级联删除 S3 上的源文件和所有关联解析的图片
- **删除解析记录时**：仅删除该解析关联的 S3 图片，不删除原始文件

## 导出流程设计

### 按文件 ID 导出（使用最新 parse）

```
GET /admin/document-files/{id}/export/markdown
GET /admin/document-files/{id}/export/html
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 未解析或非 parsed 返回 422
  ├─ 3. 获取 markdown_content
  ├─ 4. 获取关联图片列表
  ├─ 5. 替换路径（MD: S3→相对, HTML: S3→base64）
  │    支持的替换格式：
  │      - ![alt](S3Path)          → ![alt](images/name)
  │      - <img src="S3Path">       → <img src="images/name">
  │      - <img src='S3Path'>       → <img src='images/name'>
  ├─ 6. 下载图片到 ZIP images/ 目录（MD 导出）
  └─ 7. 返回 ZIP/HTML

### 按解析 ID 导出

```
GET /admin/document-parses/{parseId}/export/markdown
GET /admin/document-parses/{parseId}/export/html
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 校验 status=parsed → 非 parsed 返回 422
  ├─ 3. 查询关联的 document_file 获取文件名
  ├─ 4. 获取 markdown_content 和关联图片
  ├─ 5. 替换路径（MD: S3→相对, HTML: S3→base64）
  └─ 6. 返回 ZIP/HTML
```

## 依赖的外部模块

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentFileService` | 文件 CRUD | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `IDocumentParseService` | 解析 CRUD、状态更新、列表、删除 | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `MinerUPrecisionClient` | MinerU API 提交/轮询/下载 | `Ruoyu.Study.DocLibrary.Service` |
| `IPdfSplitService` | PDF 页数检测、拆分 | `Ruoyu.Study.DocLibrary.Service` |
| `IFileConversionService` | 非 PDF 转 PDF（LibreOffice） | `Ruoyu.Study.DocLibrary.Service` |
| `IOssService` | S3 上传/下载/删除/presigned URL | `Ruoyu.Study.Common.Oss` |

## DI 注册

```csharp
// Program.cs
builder.Services.AddScoped<IDocumentFileRepository, DocumentFileRepository>();
builder.Services.AddScoped<IDocumentParseRepository, DocumentParseRepository>();
builder.Services.AddScoped<IDocumentParseImageRepository, DocumentParseImageRepository>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();
builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddSingleton<IPdfSplitService, PdfSplitService>();
builder.Services.AddSingleton<IFileConversionService, LibreOfficeConversionService>();
builder.Services.AddHostedService<MinerUFileParseWorker>();
```
