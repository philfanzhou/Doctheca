# DocumentManagement — 设计说明

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentFileModel.cs                   # 文件领域模型
│   │   │   ├── DocumentParseStatus.cs                 # 解析状态常量（pending/parsing/parsed/failed）
│   │   │   └── DocumentParseModel.cs                  # 解析领域模型（详情/列表时引用）
│   │   ├── Services/
│   │   │   ├── IDocumentFileService.cs                 # 文件领域服务接口
│   │   │   ├── DocumentFileService.cs                  # 文件领域服务实现
│   │   │   ├── IDocumentParseService.cs                # 解析领域服务接口（触发解析/获取状态）
│   │   │   └── DocumentParseService.cs                 # 解析领域服务实现
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs              # 文件仓储接口
│   │       ├── IDocumentParseRepository.cs             # 解析仓储接口
│   │       ├── IDocumentParseImageRepository.cs        # 解析图片仓储接口
│   │       └── ISearchIndexService.cs                  # OpenSearch 索引接口
│   ├── Service/
│   │   ├── MarkdownExportHelper.cs                    # Markdown 图片路径→presigned URL 替换
│   │   └── Endpoints/
│   │       └── DocumentFileEndpoints.cs                # HTTP 端点映射
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentFileEntity.cs                   # 文件实体（document_files 表）
│       │   ├── DocumentParseEntity.cs                  # 解析实体（document_parses 表）
│       │   └── DocumentParseImageEntity.cs             # 解析图片实体（document_parse_images 表）
│       ├── Repositories/
│       │   └── DocumentFileRepository.cs               # 文件仓储实现
│       └── DatabaseInitializer.cs                     # 原生 SQL 建表（CREATE TABLE IF NOT EXISTS）
└── docs/modules/DocumentManagement/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (本文档)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名

### IDocumentFileService

```csharp
Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
Task<DocumentFileModel?> GetByIdAsync(Guid id);
Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? fileName = null);
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
Task<bool> DeleteAsync(Guid id);
```

### IDocumentParseService（本模块引用的方法）

```csharp
Task<DocumentParseModel> CreateAsync(Guid documentFileId, string modelVersion = "vlm");
Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
```

### ISearchIndexService（本模块引用的方法）

```csharp
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
Task DeleteDocumentFileIndexAsync(Guid documentFileId);
```

### HTTP 端点

```
POST   /admin/document-files/upload          上传文档文件
GET    /admin/document-files                  分页列出文档文件
GET    /admin/document-files/{id}             获取文档文件详情
PUT    /admin/document-files/{id}/metadata    更新元数据（subject/grade/year）
DELETE /admin/document-files/{id}             删除文件 + 联动清理
POST   /admin/document-files/{id}/parse       触发 MinerU 解析
```

## 数据流

### 上传流程 (POST /upload)

1. HTTP 层校验 multipart/form-data、file 非空、size ≤ 200MB、ContentType 在白名单
2. 打开文件流，生成 `objectName = $"{Guid.NewGuid()}{ext}"`
3. `IOssService.UploadAsync(stream, objectName, contentType, OssBucket.Documents, "doclibrary-files")` → 返回 `filePath`
4. 构建 `DocumentFileModel`（`CreatedBy = null`）
5. `IDocumentFileService.CreateAsync(model)` → Repository 写入 `document_files` 表，回写 `model.Id`
6. 返回 `{ id, fileName, contentType }`

### 列表流程 (GET /)

1. 根据 `parseStatus` 是否为空决定 `fetchSize`（空 = pageSize，非空 = 200）
2. 分页拉取 `IDocumentFileService.GetListAsync(fetchPage, fetchSize, fileName)`
3. 每个文件调用 `IDocumentParseService.GetLatestByFileIdAsync(f.Id)` 获取最新解析状态
4. 内存中按 `parseStatus` 过滤（`unparsed` → `ParseStatus == null`）
5. 内存分页（Skip/Take），返回 `{ data, total, page, pageSize, totalPages }`

### 详情流程 (GET /{id})

1. `IDocumentFileService.GetByIdAsync(id)` → null 返回 404
2. `IDocumentParseService.GetByFileIdAsync(id)` → 所有解析记录
3. 每个 parse 获取 images，Markdown 图片路径替换为 presigned URL
4. 每个 image 生成 presigned URL（有效期 3600s），失败回退到原始路径
5. 返回文件信息 + parses 数组

### 更新元数据流程 (PUT /{id}/metadata)

1. 解析 `UpdateMetadataRequest` body
2. `IDocumentFileService.UpdateMetadataAsync(id, subject, grade, year)` → Repository 仅更新非 null 字段
3. Best-effort: `ISearchIndexService.UpdateDocumentFileMetadataAsync` 刷新 OpenSearch
4. 返回更新后的文档信息

### 删除流程 (DELETE /{id})

1. 获取文件 → null 返回 404
2. **Step 1 — 收集 OSS 路径**:源文件 filePath + 所有 parse 的 zipPath + 所有 image 的 imagePath（HashSet 去重）
3. **Step 2 — 删除 DB 记录**:`IDocumentFileService.DeleteAsync(id)`（EF Core 级联删除解析/图片记录）
4. **Step 3 — Best-effort OSS 清理**:逐条删除，失败记 Warning
5. **Step 4 — Best-effort OpenSearch 删除**:按 `document_file_id` 删除索引
6. 返回 `{ id, deleted: true, ossDeleted, ossFailed }`

### 触发解析流程 (POST /{id}/parse)

1. 校验 `modelVersion ∈ { "vlm", "pipeline" }`
2. 校验文件存在
3. 检查同 modelVersion 无 pending/parsing 状态的解析
4. 校验 `MinerUOptions.ApiToken` 非空
5. `IDocumentParseService.CreateAsync(id, modelVersion)` → 写入 `document_parses`（`status = "pending"`）
6. 返回 `{ id, parseId, status, modelVersion }`

## 设计决策与理由

### 1. 文件本体存储于 OSS，DB 仅存路径

- **理由**:文件可能很大（最大 200MB），存入 DB 会导致表膨胀、备份慢、查询性能下降。OSS（S3/MinIO）专为大对象存储设计，成本更低
- **代价**:删除时需要联动清理 OSS，且 OSS 清理是 best-effort（失败不阻塞）

### 2. 删除时先收集路径，再删 DB，最后清 OSS

- **理由**:如果先删 DB，则无法通过关联查询获取所有 OSS 路径（尤其是图片路径需要通过 parse → image 关联获取）。先收集路径可确保完整清理
- **实现**:使用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)` 去重，避免同一路径重复删除

### 3. parseStatus 过滤在内存中完成，非 DB 查询

- **理由**:`parseStatus` 不是 `document_files` 表的字段，而是关联 `document_parses` 表的最新状态。早期实现为简化查询逻辑，拉取数据后在内存中过滤
- **代价**:当数据量大时（fetchSize=200 可能不足）需要多次拉取。当前实现通过循环拉取（`while(true)` + break 条件）缓解

### 4. 触发解析前检查同 modelVersion 的进行中状态

- **理由**:同一文件可能在 vlm 和 pipeline 两种模式下分别解析，但同一模式下不允许重复触发。避免重复提交 MinerU 任务浪费资源
- **状态定义**:`DocumentParseStatus.Pending = "pending"`、`DocumentParseStatus.Parsing = "parsing"`

### 5. 无应用层认证

- **理由**:DocLibrary 是内网管理后台，访问控制由部署层网络隔离实现（仅内网可访问 :5012 端口）。2026-07-04 移除 JWT 后 `created_by` 字段保留为 null
- **注意**:所有 `/admin/*` 端点 `AllowAnonymous`，后续接入审计场景时再恢复 `created_by`

## 外部依赖

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IOssService` | 文件上传/删除/presigned URL | `Ruoyu.Study.Common.Oss` |
| `IDocumentParseService` | 解析记录查询/创建/图片查询 | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `ISearchIndexService` | OpenSearch 索引更新/删除 | `Ruoyu.Study.DocLibrary.Service` |
| `MarkdownExportHelper` | Markdown 图片路径→presigned URL | `Ruoyu.Study.DocLibrary.Service` |
| `MinerUOptions` | MinerU ApiToken/BaseUrl 配置 | `Ruoyu.Study.DocLibrary.Service` |

## DI 注册

```csharp
// Program.cs
builder.Services.AddScoped<IDocumentFileRepository, DocumentFileRepository>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
// OSS: USE_LOCAL_OSS=1 时注册 LocalFileOssService，否则注册 S3OssService
```
