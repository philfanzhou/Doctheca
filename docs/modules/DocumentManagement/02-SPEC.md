# 02-SPEC — DocumentManagement 详细规格

## 1. 数据库变更

### 1.1 document_files 表字段

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PRIMARY KEY | 文件唯一标识，`Guid.NewGuid()` |
| `file_name` | VARCHAR(500) | NOT NULL | 文件名 |
| `file_path` | VARCHAR(500) | NOT NULL | OSS 存储路径（用户上传的原文件） |
| `content_type` | VARCHAR(100) | NOT NULL | MIME 类型（如 `application/pdf`） |
| `created_by` | UUID | NULL | 上传者用户 ID（当前为 null，内网无认证） |
| `subject` | VARCHAR(50) | NULL | 学科元数据 |
| `grade` | VARCHAR(20) | NULL | 年级元数据 |
| `year` | VARCHAR(10) | NULL | 年份元数据 |
| `created_at` | TIMESTAMP WITH TIME ZONE | NOT NULL, DB 生成 | 创建时间 |
| `updated_at` | TIMESTAMP WITH TIME ZONE | NULL, DB 计算 | 最后更新时间（`[ConcurrencyCheck]`） |

> 字段约束以 `DocumentFileEntity.cs` 的 `[Column]`/`[MaxLength]`/`[Required]` 特性为准。本表通过 `DatabaseInitializer`（`CREATE TABLE IF NOT EXISTS`）创建，**无 EF Core Migration**。

### 1.2 实体与模型映射

```csharp
// DocumentFileEntity.cs
[Table("document_files")]
public class DocumentFileEntity
{
    [Key][Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();
    [Column("file_name")][Required][MaxLength(500)]
    public string FileName { get; set; } = string.Empty;
    [Column("file_path")][Required][MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;
    [Column("content_type")][Required][MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    [Column("created_by")]
    public Guid? CreatedBy { get; set; }
    [Column("subject")][MaxLength(50)]
    public string? Subject { get; set; }
    [Column("grade")][MaxLength(20)]
    public string? Grade { get; set; }
    [Column("year")][MaxLength(10)]
    public string? Year { get; set; }
    [Column("created_at")][DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at")][ConcurrencyCheck][DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

`DocumentFileModel` 字段与 Entity 完全对应。`DocumentFileRepository.MapToEntity` / `MapToModel` 做双向映射，无 AutoMapper。

## 2. 接口变更

### 2.1 IDocumentFileRepository

```csharp
Task AddAsync(DocumentFileModel model);
Task<DocumentFileModel?> GetByIdAsync(Guid id);
Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
Task<bool> UpdateAsync(DocumentFileModel model);
Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
Task<bool> DeleteAsync(Guid id);
```

### 2.2 IDocumentFileService

```csharp
Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
Task<DocumentFileModel?> GetByIdAsync(Guid id);
Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
Task<bool> DeleteAsync(Guid id);
```

### 2.3 ISearchIndexService（本模块引用）

```csharp
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
Task DeleteDocumentFileIndexAsync(Guid documentFileId);
```

## 3. HTTP 端点

### 3.1 端点路由注册

```csharp
// DocumentFileEndpoints.cs
var group = app.MapGroup("/admin/document-files");
group.MapPost("/upload", UploadDocumentFile).WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));
group.MapGet("/", ListDocumentFiles);
group.MapGet("/{id:guid}", GetDocumentFile);
group.MapPost("/{id:guid}/parse", ParseDocumentFile);
group.MapPut("/{id:guid}/metadata", UpdateDocumentFileMetadata);
group.MapDelete("/{id:guid}", DeleteDocumentFile);
```

### 3.2 POST /admin/document-files/upload

| 项 | 值 |
|----|---|
| Content-Type | `multipart/form-data` |
| form file 字段名 | `file` |
| RequestSizeLimit | 200MB（`RequestSizeLimitAttribute` + `FormOptions.MultipartBodyLengthLimit`） |

**校验**:1) `HasFormContentType` 2) file 非空 3) 大小 ≤ 200MB 4) ContentType 在允许列表中。

**MIME 白名单** (`DocumentFileMimeTypes`):
- `application/pdf`
- `application/msword`
- `application/vnd.openxmlformats-officedocument.wordprocessingml.document`
- `application/vnd.ms-powerpoint`
- `application/vnd.openxmlformats-officedocument.presentationml.presentation`

**OSS 上传参数**:`UploadAsync(stream, objectName, contentType, OssBucket.Documents, "doclibrary-files")`，其中 `objectName = $"{Guid.NewGuid()}{ext}"`。

**Response 200**:
```json
{ "success": true, "data": { "id": "...", "fileName": "...", "contentType": "..." } }
```

**错误码**:
- `DOCLIBRARY_FILE_REQUIRED` (400) — 空文件
- `DOCLIBRARY_FILE_FORMAT_UNSUPPORTED` (400) — 格式不支持
- 400 — 超过 200MB / 非 multipart

### 3.3 GET /admin/document-files

**Query 参数**:`page`(int, 默认 1)、`pageSize`(int, 默认 20)、`parseStatus`(string?, 可选)、`fileName`(string?, 可选)。

**Response 200**:
```json
{
  "success": true,
  "data": [{ "id": "...", "fileName": "...", "contentType": "...", "parseStatus": "...", "errorMessage": "...", "createdBy": "...", "createdAt": "...", "parsedAt": "..." }],
  "total": 0, "page": 1, "pageSize": 20, "totalPages": 0
}
```

### 3.4 GET /admin/document-files/{id}

**Response 200** — 文件信息 + `parses` 数组（每条含 `images`，presigned URL 有效期 3600 秒）。Markdown 内容中图片路径经 `MarkdownExportHelper.ReplaceImagePathsPresignedAsync` 替换为 presigned URL。

**错误码**:`DOCLIBRARY_FILE_NOT_FOUND` (404)。

### 3.5 PUT /admin/document-files/{id}/metadata

**Request body** (`UpdateMetadataRequest`):
```json
{ "subject": "English", "grade": "G10", "year": "2024" }
```
全部字段可选；`null` 表示不修改，`""`(空字符串)写入 DB。

**DTO 定义** (`DocumentFileEndpoints.cs`):
```csharp
public record UpdateMetadataRequest
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
```

**Response 200**:更新后的文档信息（id/fileName/subject/grade/year/updatedAt）。

**错误码**:`DOCLIBRARY_FILE_NOT_FOUND` (404)；400 — 请求体解析失败。

### 3.6 DELETE /admin/document-files/{id}

**Response 200**:
```json
{ "success": true, "data": { "id": "...", "deleted": true, "ossDeleted": 0, "ossFailed": 0 } }
```

**错误码**:`DOCLIBRARY_FILE_NOT_FOUND` (404)。

### 3.7 POST /admin/document-files/{id}/parse

**Query 参数**:`modelVersion`(string, 默认 `vlm`)。

**校验序列**:1) modelVersion ∈ {`vlm`, `pipeline`} 2) 文件存在 3) 同 modelVersion 无 pending/parsing 状态的解析 4) `MinerUOptions.ApiToken` 非空。

**Response 200**:
```json
{ "success": true, "data": { "id": "...", "parseId": "...", "status": "pending", "modelVersion": "vlm" } }
```

**错误码**:
- `DOCLIBRARY_INVALID_MODEL_VERSION` (400) — modelVersion 不合法
- `DOCLIBRARY_FILE_NOT_FOUND` (404) — 文件不存在
- `DOCLIBRARY_PARSE_IN_PROGRESS` (422) — 同 modelVersion 有进行中的解析
- `DOCLIBRARY_MINERU_NOT_CONFIGURED` (503) — MinerU ApiToken 未配置

## 4. 实现规格

### 4.1 UploadDocumentFile

```
1. 校验 request.HasFormContentType
2. 读取 form，取 file = form.Files.GetFile("file")
3. 校验 file 非空、size ≤ MaxFileSize、ContentType ∈ DocumentFileMimeTypes
4. 打开 stream，ext = Path.GetExtension(file.FileName) ?? ".bin"
5. objectName = $"{Guid.NewGuid()}{ext}"
6. filePath = await ossService.UploadAsync(stream, objectName, contentType, OssBucket.Documents, "doclibrary-files")
7. 构建 DocumentFileModel { FileName, FilePath, ContentType, CreatedBy = null }
8. created = await fileService.CreateAsync(model)
9. 返回 200 { id, fileName, contentType }
```

> `CreatedBy = null` 注释:DocLibrary 是内网管理后台,无应用层认证(2026-07-04 移除 JWT)。`created_by` 字段保留为 null。

### 4.2 ListDocumentFiles

```
1. 若 parseStatus 为空: fetchSize = pageSize; 否则 fetchSize = 200
2. 分页拉取 fileService.GetListAsync(fetchPage, fetchSize, fileName)
3. 对每个文件调用 parseService.GetLatestByFileIdAsync(f.Id) 获取最新解析状态
4. 内存中组装 (File, ParseStatus, ErrorMessage, ParsedAt)
5. 按 parseStatus 过滤:
   - "unparsed" → ParseStatus == null
   - 其他 → ParseStatus == parseStatus
6. 内存分页: filtered.Skip((page-1)*pageSize).Take(pageSize)
7. 返回 200 { data, total, page, pageSize, totalPages }
```

> 注意:parseStatus 过滤在内存中完成,非 DB 查询。大批量数据时 fetchSize=200 可能不足(循环拉取直到 items.Count < fetchSize 或 allEnriched.Count >= totalCount)。

### 4.3 GetDocumentFile

```
1. file = await fileService.GetByIdAsync(id); null → 404
2. allParses = await parseService.GetByFileIdAsync(id)
3. 遍历每个 parse:
   a. images = await parseService.GetImagesByParseIdAsync(parse.Id)
   b. markdownContent 中的图片路径替换为 presigned URL
   c. 对每个 image 生成 presigned URL (有效期 3600s)，失败时回退到原始 ImagePath
   d. 组装 parseResult（含 id/modelVersion/status/markdownContent/contentList/contentListV2/modelJson/layoutJson/errorMessage/parsedAt/images）
4. 返回 200 { id, fileName, contentType, createdAt, parses }
```

### 4.4 UpdateDocumentFileMetadata

```
1. 解析 request body 为 UpdateMetadataRequest（失败 → 400）
2. updated = await fileService.UpdateMetadataAsync(id, body.Subject, body.Grade, body.Year)
3. null → 404
4. try: await searchIndexService.UpdateDocumentFileMetadataAsync(id, updated.Subject, updated.Grade, updated.Year)
   catch: log warning（不阻塞）
5. 返回 200 { id, fileName, subject, grade, year, updatedAt }
```

### 4.5 DeleteDocumentFile

```
1. file = await fileService.GetByIdAsync(id); null → 404
2. Step 1: 收集 OSS 路径
   - ossPaths = { file.FilePath } (HashSet, OrdinalIgnoreCase)
   - allParses = await parseService.GetByFileIdAsync(id)
   - 对每个 parse: ossPaths.Add(parse.ZipPath); 对每个 image: ossPaths.Add(img.ImagePath)
3. Step 2: await fileService.DeleteAsync(id)（级联删除解析/图片 DB 记录）
4. Step 3: 遍历 ossPaths，try ossService.DeleteAsync(path)，失败记入 failedPaths
5. Step 4: try searchIndexService.DeleteDocumentFileIndexAsync(id) catch log warning
6. 返回 200 { id, deleted = true, ossDeleted, ossFailed }
```

### 4.6 ParseDocumentFile

```
1. 校验 modelVersion ∈ { "vlm", "pipeline" }（否则 400 → `DOCLIBRARY_INVALID_MODEL_VERSION`）
2. file = await fileService.GetByIdAsync(id); null → 404
3. latestParse = await parseService.GetLatestByFileIdAndModelAsync(id, modelVersion)
4. if latestParse.Status ∈ { Pending, Parsing } → 422 DOCLIBRARY_PARSE_IN_PROGRESS
5. if string.IsNullOrEmpty(minerUOptions.ApiToken) → 503 DOCLIBRARY_MINERU_NOT_CONFIGURED
6. parse = await parseService.CreateAsync(id, modelVersion)（status = "pending"）
7. 返回 200 { id, parseId, status, modelVersion }
```

## 5. 错误处理表

| 场景 | HTTP | 错误码 | 处理 |
|------|------|--------|------|
| 非 multipart 请求 | 400 | — | "Request must be multipart/form-data" |
| 空文件 | 400 | `DOCLIBRARY_FILE_REQUIRED` | 拒绝上传 |
| 文件超 200MB | 400 | — | "File size exceeds 200MB limit" |
| 格式不支持 | 400 | `DOCLIBRARY_FILE_FORMAT_UNSUPPORTED` | 拒绝上传 |
| 文件不存在 | 404 | `DOCLIBRARY_FILE_NOT_FOUND` | 返回 404 |
| modelVersion 不合法 | 400 | `DOCLIBRARY_INVALID_MODEL_VERSION` | "must be 'vlm' or 'pipeline'" |
| 同 modelVersion 解析进行中 | 422 | `DOCLIBRARY_PARSE_IN_PROGRESS` | 拒绝重复触发 |
| MinerU 未配置 | 503 | `DOCLIBRARY_MINERU_NOT_CONFIGURED` | "MinerU API Token not configured" |
| 元数据请求体解析失败 | 400 | — | "Invalid request body" |
| OSS 删除失败 | — | — | 记 Warning，不阻塞 |
| OpenSearch 删除失败 | — | — | 记 Warning，不阻塞 |
| presigned URL 生成失败 | — | — | 回退到原始 ImagePath，记 Warning |

## 6. 测试策略

### 6.1 单元测试 (UT)

位于 `DocumentFileServiceTests.cs` 和 `DocumentFileDeleteCleanupTests.cs`。

| # | 测试文件 | 测试方法 | 覆盖 |
|---|---------|---------|------|
| UT-DF-01 | DocumentFileServiceTests | `CreateAsync_SetsCreatedAtAndReturnsModel` | FR-01 |
| UT-DF-02 | DocumentFileServiceTests | `GetByIdAsync_ReturnsModel_WhenExists` | FR-03 |
| UT-DF-03 | DocumentFileServiceTests | `GetByIdAsync_ReturnsNull_WhenNotExists` | FR-03 |
| UT-DF-04 | DocumentFileServiceTests | `GetListAsync_ReturnsPagedResults` | FR-02 |
| UT-DF-05 | DocumentFileServiceTests | `DeleteAsync_DelegatesToRepository` | FR-05 |
| UT-DF-06 | DocumentFileDeleteCleanupTests | `Cleanup_CollectsAllOssPaths_BeforeDeletion` | FR-05 |
| UT-DF-07 | DocumentFileDeleteCleanupTests | `Cleanup_HandlesNullZipPath` | FR-05 |
| UT-DF-08 | DocumentFileDeleteCleanupTests | `Cleanup_DedupesDuplicatePaths` | FR-05 |

### 6.2 集成测试 (规划)

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-DF-01 | 上传 PDF 文件成功，DB 有记录，OSS 有文件 | AC-01 |
| IT-DF-02 | 上传不支持格式返回 400 | AC-02 |
| IT-DF-03 | 上传空文件返回 400 | AC-03 |
| IT-DF-04 | 上传超 200MB 返回 400 | AC-04 |
| IT-DF-05 | 列表分页 + fileName 搜索 + parseStatus 过滤 | AC-05 |
| IT-DF-06 | 详情返回 parses + 图片 presigned URL | AC-06 |
| IT-DF-07 | 触发解析创建 pending 记录 | AC-07 |
| IT-DF-08 | 重复触发同 modelVersion 返回 422 | AC-08 |
| IT-DF-09 | MinerU 未配置时触发返回 503 | AC-09 |
| IT-DF-10 | 更新元数据后 OpenSearch 索引同步 | AC-10 |
| IT-DF-11 | 删除文件清理 DB + OSS + OpenSearch | AC-11 |
| IT-DF-12 | 删除不存在文件返回 404 | AC-12 |

## 7. 影响范围

| 组件 | 影响 |
|------|------|
| `document_files` 表 | 文件元数据存储 |
| `DocumentFileEndpoints` | 6 个 HTTP 端点 |
| `DocumentFileService` / `IDocumentFileService` | 文件领域服务 |
| `DocumentFileRepository` / `IDocumentFileRepository` | 文件仓储 |
| `OpenSearchIndexService` | 删除 / 更新元数据时联动 |
| `DocumentParseService` | 获取解析状态 / 创建解析记录 |
| 前端管理页 | 文件列表 / 上传 / 详情 / 元数据编辑 / 删除 / 触发解析 |
