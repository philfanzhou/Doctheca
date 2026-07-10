# QuestionBankImport — 设计说明

## 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Service/
│   │   ├── QuestionBankImportService.cs                # 核心业务逻辑
│   │   └── Endpoints/
│   │       └── QuestionBankImportEndpoints.cs           # 4 个 HTTP 端点
│   ├── Domain/
│   │   ├── Services/
│   │   │   └── IQuestionBankImportService.cs            # 服务接口 + record 定义
│   │   └── Repositories/
│   │       └── IDocumentParseImportRepository.cs         # 导入状态仓储
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentParseImportEntity.cs              # 导入状态实体
│       │   ├── DocumentParseBlockEntity.cs               # block 实体（复用）
│       │   └── DocumentParseImageEntity.cs               # 图片实体（复用）
│       └── Repositories/
│           └── DocumentParseImportRepository.cs          # 导入状态仓储实现
└── docs/modules/QuestionBankImport/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (本文档)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 接口设计

### 4 个 HTTP 端点

| 方法 | 路径 | 服务方法 | 说明 |
|------|------|---------|------|
| GET | `/admin/document-parses/importable` | `GetImportableListAsync` | 分页查询可导入 parse 列表 |
| GET | `/admin/document-parses/{parseId}/blocks` | `GetBlocksAsync` | 按 parseId 分页获取结构化块 |
| GET | `/admin/document-parses/images/{imageId}` | `GetImageBlobAsync` | 获取图片二进制流 |
| POST | `/admin/document-parses/{parseId}/import-status` | `UpsertImportStatusAsync` | 回写导入状态 |

所有端点 `AllowAnonymous`，内网直连无鉴权。

### 响应格式

统一遵循 DocLibrary 分页封装：

```json
// 列表响应
{ "success": true, "data": [...], "total": N, "page": 1, "pageSize": 20, "totalPages": 1 }

// 错误响应
{ "success": false, "message": "...", "errorCode": "..." }
```

### IQuestionBankImportService 接口

```csharp
Task<(List<ImportableParseItem> Items, int TotalCount)> GetImportableListAsync(
    int page, int pageSize, string? search, bool includeImported);

Task<(List<ParseBlockItem> Items, int TotalCount)> GetBlocksAsync(
    Guid parseId, int? pageId, string? blockType, int page, int pageSize);

Task<ParseImageBlob> GetImageBlobAsync(Guid imageId);

Task<ImportStatusResult> UpsertImportStatusAsync(
    Guid parseId, Guid importedBy, string status, string? note, string? importedQuestionIds);
```

## 数据流

### 可导入列表查询

```
GET /admin/document-parses/importable
  │
  ├─ 1. EF Core 查询：document_parses JOIN document_files LEFT JOIN document_parse_imports
  ├─ 2. WHERE status = 'parsed'
  ├─ 3. [可选] WHERE file_name ILIKE '%search%'
  ├─ 4. [可选] 排除 import_status = 'imported'（includeImported=false 时）
  ├─ 5. ORDER BY parsed_at DESC NULLS LAST
  └─ 6. 返回分页结果
```

### 结构化块查询

```
GET /admin/document-parses/{parseId}/blocks
  │
  ├─ 1. 验证 parse 存在且 status=parsed
  ├─ 2. EF Core 查询：document_parse_blocks WHERE parse_id = ?
  ├─ 3. [可选] WHERE page_id = ?
  ├─ 4. [可选] WHERE block_type = ?
  ├─ 5. Include(b => b.Image) 预加载（N+1 防护）
  ├─ 6. 并行生成 presigned URL（image 类型 block）
  └─ 7. blockData JSON 反序列化（非法 JSON 返回 null）
```

### 图片访问

```
GET /admin/document-parses/images/{imageId}
  │
  ├─ 1. 查询 document_parse_images 记录
  ├─ 2. IOssService.DownloadAsync(imagePath)
  └─ 3. Results.Stream(stream, contentType, imageName)
```

### 导入状态回写

```
POST /admin/document-parses/{parseId}/import-status
  │
  ├─ 1. 验证 status ∈ {imported, failed}
  ├─ 2. 验证 importedBy 非空 GUID
  ├─ 3. 查询 document_parse_imports 现有记录
  ├─ 4. [已有记录] 状态覆盖逻辑：
  │     ├─ imported → imported：拒绝（422）
  │     ├─ imported → failed：允许
  │     └─ failed → imported/failed：允许
  └─ 5. [无记录] 插入新记录
```

## N+1 防护

`GetBlocksAsync` 方法使用两个层次的 N+1 防护：

1. **EF Core Include**：`_dbContext.DocumentParseBlocks.Include(b => b.Image)` — 预加载关联图片，避免逐块查询
2. **并行 presigned URL**：`Task.WhenAll` 批量生成 presigned URL，避免顺序 OSS 调用

## 错误处理

| 错误码 | HTTP | 场景 | 处理 |
|--------|------|------|------|
| `DOCLIBRARY_PARSE_NOT_FOUND` | 404 | parseId 不存在 | KeyNotFoundException → 404 |
| `DOCLIBRARY_PARSE_NOT_PARSED` | 422 | status ≠ parsed | InvalidOperationException → 422 |
| `DOCLIBRARY_IMAGE_NOT_FOUND` | 404 | imageId 不存在 | KeyNotFoundException → 404 |
| `DOCLIBRARY_OSS_DOWNLOAD_FAILED` | 500 | OSS 下载失败 | 异常向上抛 → 500 |
| `DOCLIBRARY_IMPORT_STATUS_INVALID` | 400 | 参数非法 | ArgumentException → 400 |
| `DOCLIBRARY_PARSE_ALREADY_IMPORTED` | 422 | 重复 imported | InvalidOperationException → 422 |

## 外部依赖

| 依赖 | 用途 |
|------|------|
| `IOssService` | 图片下载 + presigned URL 生成 |
| EF Core + Npgsql | 数据库查询（JOIN / Include） |

## DI 注册

```csharp
builder.Services.AddScoped<IDocumentParseImportRepository, DocumentParseImportRepository>();
builder.Services.AddScoped<IQuestionBankImportService, QuestionBankImportService>();
```

### 端点参数绑定约定（Bug 修复记录 2026-07-10）

`QuestionBankImportEndpoints.cs` 的四个静态方法通过 DI 接收 `IQuestionBankImportService importService` 参数。.NET 8 minimal API 对复杂类型参数默认推断为 Body（Inferred），若方法不允许 inferred body parameters 会抛 `System.InvalidOperationException: Body was inferred but the method does not allow inferred body parameters.`

**修复要求**：所有通过 DI 解析的服务参数必须显式标注 `[FromServices]`，否则路由元数据推断阶段失败，首个请求即报 500。

```csharp
// 正确写法
private static async Task<IResult> ListImportableParses(
    [FromServices] IQuestionBankImportService importService,
    [FromQuery] int page = 1,
    ...)

// 错误写法（会触发 Body inferred 异常）
private static async Task<IResult> ListImportableParses(
    IQuestionBankImportService importService,   // 缺 [FromServices]
    [FromQuery] int page = 1,
    ...)
```

四个端点方法（`ListImportableParses` / `GetParseBlocks` / `GetImage` / `UpsertImportStatus`）的 `importService` 参数均已补 `[FromServices]`。`GetParseBlocks` / `GetImage` / `UpsertImportStatus` 原本已对 `ILoggerFactory` 标注 `[FromServices]`，此次补齐 `importService`。

> **历史现状（已修复）**：2026-07-10 部署验证发现 `Program.cs` 未注册 `IQuestionBankImportService`，且四个端点方法的 `importService` 参数缺 `[FromServices]`。首个请求 `/admin/document-parses/importable` 报 `Body was inferred` 异常。已修复：Program.cs 补 `AddScoped<IQuestionBankImportService, QuestionBankImportService>()`，四个方法补 `[FromServices]`。
