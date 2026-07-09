# QuestionBank 拉模式导入接口 — 详细需求规格 (SPEC)

## 功能概述和用户故事

**核心用户故事**：作为 QuestionBank 服务，我需要通过 HTTP API 拉取 DocLibrary 已完成的 MinerU 解析产出（可导入列表、结构化块、图片），并回写导入状态以避免重复处理，以便自主完成试卷到题库的拆分入库。

**补充约束**：
- 本功能仅面向内网服务调用方（QuestionBank 内网直连 DocLibrary），不开放给前台用户。
- 所有接口 `AllowAnonymous`，与现有 DocLibrary Admin API 风格一致（内网管理后台无应用层认证）。
- 本模块只读 MinerU 解析链路数据（`document_parses` / `document_parse_blocks` / `document_parse_images`）。
- 元数据过滤（subject/grade/year）不在本次范围。QuestionBank 拉取后可自行根据 `fileName` 或拉到的内容判断。

## 功能要求清单（编号 FR-01 起）

| 编号 | 功能要求 | 对应端点 |
|------|---------|----------|
| FR-01 | 分页查询可导入的 parse 列表（`status=parsed`），返回 parse + 文件 + 导入状态信息 | `GET /admin/document-parses/importable` |
| FR-02 | 按文件名模糊搜索可导入 parse 列表 | `GET /admin/document-parses/importable?search=` |
| FR-03 | 通过 `includeImported` 参数控制是否包含已标记 `imported` 的 parse（默认排除） | `GET /admin/document-parses/importable?includeImported=true` |
| FR-04 | 按 parseId 分页获取结构化块（`document_parse_blocks`） | `GET /admin/document-parses/{parseId}/blocks` |
| FR-05 | 按 `pageId` 过滤结构化块 | `GET /admin/document-parses/{parseId}/blocks?pageId=0` |
| FR-06 | 按 `blockType` 过滤结构化块（text/image/table/title 等） | `GET /admin/document-parses/{parseId}/blocks?blockType=text` |
| FR-07 | 结构化块返回 `blockData` 为解析后的 JSON 对象（非原始字符串） | 同 FR-04 |
| FR-08 | image 类型 block 额外返回 `imageName` / `imagePath` / `imageUrl`（presigned URL） | 同 FR-04 |
| FR-09 | 按 imageId 获取图片二进制流（从 OSS 下载，返回正确 MIME） | `GET /admin/document-parses/images/{imageId}` |
| FR-10 | 回写 parse 的导入状态（首次插入或更新） | `POST /admin/document-parses/{parseId}/import-status` |
| FR-11 | 防重复导入：当前 parse 已是 `imported` 状态时，再次标记 `imported` 返回 422 | 同 FR-10 |
| FR-12 | 允许状态覆盖：`imported` → `failed`（重试失败）和 `failed` → `imported`（重试成功） | 同 FR-10 |

## 详细的验收标准（编号 AC-FR-01 起）

### AC-FR-01：分页查询可导入列表

**Given** 系统中存在若干 `status=parsed` 的 parse 记录
**When** 调用 `GET /admin/document-parses/importable?page=1&pageSize=20`
**Then** 返回 HTTP 200，响应体：
```json
{
  "success": true,
  "data": [
    {
      "parseId": "uuid",
      "fileId": "uuid",
      "fileName": "2024-英语-期末.pdf",
      "modelVersion": "vlm",
      "parsedAt": "2026-07-04T10:00:00.000Z",
      "importStatus": null,
      "importedAt": null
    }
  ],
  "total": 50,
  "page": 1,
  "pageSize": 20,
  "totalPages": 3
}
```

**Given** 调用方未传 page 或 pageSize
**When** 调用 `GET /admin/document-parses/importable`
**Then** 使用默认值 page=1、pageSize=20

**Given** 调用方传入 page ≤ 0
**When** 调用 `GET /admin/document-parses/importable?page=0`
**Then** page 自动修正为 1

**Given** 调用方传入 pageSize ≤ 0
**When** 调用 `GET /admin/document-parses/importable?pageSize=0`
**Then** pageSize 自动修正为 20

**Given** 调用方传入 pageSize > 100
**When** 调用 `GET /admin/document-parses/importable?pageSize=200`
**Then** pageSize 自动修正为 100

**Given** 系统中存在多个 `status=parsed` 的 parse
**When** 调用 `GET /admin/document-parses/importable`
**Then** 结果按 `parsed_at DESC` 排序（最近解析的在前）

**Given** 系统中存在 `status=pending` / `parsing` / `failed` 的 parse
**When** 调用 `GET /admin/document-parses/importable`
**Then** 仅返回 `status=parsed` 的记录，其他状态被过滤

### AC-FR-02：按文件名模糊搜索

**Given** 系统中存在 `file_name` 包含"期末"的 parse 记录
**When** 调用 `GET /admin/document-parses/importable?search=期末`
**Then** 仅返回 `file_name` 模糊匹配的记录

**Given** 调用方不传 search 参数
**When** 调用 `GET /admin/document-parses/importable`
**Then** 不做搜索过滤，返回全部 `status=parsed` 记录

### AC-FR-03：includeImported 控制

**Given** 系统中存在 parse A（无导入记录）和 parse B（`import_status=imported`）
**When** 调用 `GET /admin/document-parses/importable`（默认 `includeImported=false`）
**Then** 仅返回 parse A，parse B 被排除

**When** 调用 `GET /admin/document-parses/importable?includeImported=true`
**Then** 返回 parse A 和 parse B，parse B 的 `importStatus="imported"`、`importedAt` 不为 null

**Given** 系统中存在 parse C（`import_status=failed`）
**When** 调用 `GET /admin/document-parses/importable`（默认 `includeImported=false`）
**Then** parse C 被返回（`failed` 状态不排除，仅排除 `imported`），`importStatus="failed"`

### AC-FR-04：按 parseId 获取结构化块

**Given** 系统中存在 parseId 对应的 parse，且 `status=parsed`
**When** 调用 `GET /admin/document-parses/{parseId}/blocks?page=1&pageSize=50`
**Then** 返回 HTTP 200，响应体：
```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "parseId": "uuid",
      "pageId": 0,
      "sortIndex": 0,
      "blockType": "text",
      "textContent": "题目内容...",
      "imageName": null,
      "imagePath": null,
      "imageUrl": null,
      "blockData": { "type": "text", "text": "题目内容...", "page_id": 0, "bbox": [100, 200, 300, 400] }
    }
  ],
  "total": 120,
  "page": 1,
  "pageSize": 50,
  "totalPages": 3
}
```

**Given** 调用方未传 page 或 pageSize
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 使用默认值 page=1、pageSize=50（blocks 默认 page size 较大，因单文档块数可能很多）

**Given** 调用方传入 pageSize > 200
**When** 调用 `GET /admin/document-parses/{parseId}/blocks?pageSize=500`
**Then** pageSize 自动修正为 200

**Given** 系统中不存在指定 parseId
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 返回 HTTP 404：
```json
{
  "success": false,
  "message": "Parse record not found",
  "errorCode": "DOCLIBRARY_PARSE_NOT_FOUND"
}
```

**Given** 系统中存在指定 parseId，但 `status` 不是 `parsed`（如 `parsing` / `failed`）
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 返回 HTTP 422：
```json
{
  "success": false,
  "message": "Parse is not in parsed status",
  "errorCode": "DOCLIBRARY_PARSE_NOT_PARSED"
}
```

**Given** 系统中存在指定 parseId 且 `status=parsed`，但无 blocks 数据
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 返回 HTTP 200，`data=[]`、`total=0`、`totalPages=1`

### AC-FR-05：按 pageId 过滤

**Given** parse 含 page_id=0, 1, 2 的多个 block
**When** 调用 `GET /admin/document-parses/{parseId}/blocks?pageId=1`
**Then** 仅返回 `page_id=1` 的 block

**When** 调用 `GET /admin/document-parses/{parseId}/blocks?pageId=0`
**Then** 仅返回 `page_id=0` 的 block（pageId=0 是有效值，不应被当作"未传参"处理）

**Given** 调用方不传 pageId 参数
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 不做 pageId 过滤，返回全部 block

### AC-FR-06：按 blockType 过滤

**Given** parse 含 text/image/table/title 等多种 block
**When** 调用 `GET /admin/document-parses/{parseId}/blocks?blockType=image`
**Then** 仅返回 `block_type=image` 的 block

**Given** 调用方传入无效的 blockType（如 `blockType=invalid`）
**When** 调用 `GET /admin/document-parses/{parseId}/blocks?blockType=invalid`
**Then** 返回空列表 `data=[]`（由仓储层过滤，不报错）

### AC-FR-07：blockData 为解析后的 JSON 对象

**Given** block 的 `block_data` 字段为 JSON 字符串 `'{"type":"text","text":"hello","page_id":0}'`
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 响应中 `blockData` 字段为 JSON 对象 `{ "type": "text", "text": "hello", "page_id": 0 }`，非字符串

**Given** block 的 `block_data` 字段为空或 `'{}'`
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 响应中 `blockData` 字段为空对象 `{}`

**Given** block 的 `block_data` 字段为非法 JSON
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 响应中 `blockData` 字段为 `null`（解析失败时返回 null，不抛异常），并记录警告日志

### AC-FR-08：image 类型 block 额外字段

**Given** block 的 `block_type=image`，`image_id` 关联 `document_parse_images` 记录
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 该 block 的响应包含：
- `imageName`：图片文件名（如 `abc.jpg`）
- `imagePath`：OSS 路径
- `imageUrl`：presigned URL（有效期由 OSS 配置决定，通常 1 小时）

**Given** block 的 `block_type=text`（非 image）
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 该 block 的 `imageName` / `imagePath` / `imageUrl` 均为 `null`

**Given** block 的 `block_type=image` 但 `image_id` 为 null（数据不一致）
**When** 调用 `GET /admin/document-parses/{parseId}/blocks`
**Then** 该 block 的 `imageName` / `imagePath` / `imageUrl` 均为 `null`，并记录警告日志

### AC-FR-09：图片二进制流访问

**Given** 系统中存在指定 imageId 的图片记录
**When** 调用 `GET /admin/document-parses/images/{imageId}`
**Then** 返回 HTTP 200，`Content-Type` 为图片的 MIME 类型（如 `image/jpeg`），响应体为图片二进制流

**Given** 系统中不存在指定 imageId
**When** 调用 `GET /admin/document-parses/images/{imageId}`
**Then** 返回 HTTP 404：
```json
{
  "success": false,
  "message": "Image not found",
  "errorCode": "DOCLIBRARY_IMAGE_NOT_FOUND"
}
```

**Given** 图片记录存在，但 OSS 上的文件已被删除（数据不一致）
**When** 调用 `GET /admin/document-parses/images/{imageId}`
**Then** 返回 HTTP 500：
```json
{
  "success": false,
  "message": "Failed to download image from OSS",
  "errorCode": "DOCLIBRARY_OSS_DOWNLOAD_FAILED"
}
```

### AC-FR-10：回写导入状态（首次插入）

**Given** 系统中存在 parseId，且无导入记录
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body：
```json
{
  "importedBy": "00000000-0000-0000-0000-000000000001",
  "status": "imported",
  "note": "Imported 50 questions",
  "importedQuestionIds": ["uuid1", "uuid2", "uuid3"]
}
```
**Then** 返回 HTTP 200：
```json
{
  "success": true,
  "data": {
    "parseId": "uuid",
    "importStatus": "imported",
    "importedAt": "2026-07-04T11:00:00.000Z",
    "updatedAt": null
  }
}
```
数据库 `document_parse_imports` 表新增一条记录。

**Given** body 中 `status` 字段不是 `imported` 或 `failed`
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"invalid"}`
**Then** 返回 HTTP 400：
```json
{
  "success": false,
  "message": "Status must be 'imported' or 'failed'",
  "errorCode": "DOCLIBRARY_IMPORT_STATUS_INVALID"
}
```

**Given** body 中 `importedBy` 缺失或非合法 UUID
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"imported"}`
**Then** 返回 HTTP 400：
```json
{
  "success": false,
  "message": "importedBy is required and must be a valid UUID",
  "errorCode": "DOCLIBRARY_IMPORT_STATUS_INVALID"
}
```

**Given** 系统中不存在指定 parseId
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`
**Then** 返回 HTTP 404 + `DOCLIBRARY_PARSE_NOT_FOUND`

### AC-FR-11：防重复导入

**Given** parseId 当前 `import_status=imported`
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"imported",...}`
**Then** 返回 HTTP 422：
```json
{
  "success": false,
  "message": "Parse is already marked as imported",
  "errorCode": "DOCLIBRARY_PARSE_ALREADY_IMPORTED"
}
```

### AC-FR-12：状态覆盖

**Given** parseId 当前 `import_status=imported`
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"failed","note":"Re-import failed due to validation error"}`
**Then** 返回 HTTP 200，`importStatus="failed"`，`updatedAt` 更新为当前时间，`importedAt` 保留首次值

**Given** parseId 当前 `import_status=failed`
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"imported","note":"Re-imported successfully"}`
**Then** 返回 HTTP 200，`importStatus="imported"`，`updatedAt` 更新，`importedAt` 更新为当前时间（重试成功的标记）

**Given** parseId 当前 `import_status=failed`
**When** 调用 `POST /admin/document-parses/{parseId}/import-status`，body `{"status":"failed",...}`（重复 failed）
**Then** 返回 HTTP 200，`importStatus="failed"`，`updatedAt` 更新（允许重复 failed，覆盖 note）

## 关联查询设计

### 可导入列表的关联链

```
document_parses (status=parsed)
  ↓ document_file_id
document_files (file_name)
  ← LEFT JOIN document_parse_imports ON parse_id = document_parses.id
```

SQL 模式（伪代码）：
```sql
SELECT p.id, p.document_file_id, f.file_name, p.model_version, p.parsed_at,
       i.status AS import_status, i.created_at AS imported_at
FROM document_parses p
INNER JOIN document_files f ON p.document_file_id = f.id
LEFT JOIN document_parse_imports i ON i.parse_id = p.id
WHERE p.status = 'parsed'
  [AND f.file_name LIKE '%search%']
  [AND (i.status IS NULL OR i.status != 'imported')]  -- includeImported=false
ORDER BY p.parsed_at DESC NULLS LAST
LIMIT @pageSize OFFSET @offset;
```

> **注**：`parsed_at` 可能为 NULL（解析完成但未记录时间），用 `NULLS LAST` 保证空值排在最后。

### 结构化块的关联

```
document_parse_blocks (parse_id)
  ↓ image_id (LEFT JOIN)
document_parse_images (image_name, image_path, content_type)
```

EF Core 查询模式：
```csharp
var query = _dbContext.DocumentParseBlocks
    .Where(b => b.ParseId == parseId)
    .OrderBy(b => b.PageId).ThenBy(b => b.SortIndex);

if (pageId.HasValue) query = query.Where(b => b.PageId == pageId.Value);
if (!string.IsNullOrEmpty(blockType)) query = query.Where(b => b.BlockType == blockType);

var blocks = await query
    .Skip((page - 1) * pageSize)
    .Take(pageSize)
    .Include(b => b.Image)  // 预加载关联图片
    .ToListAsync();
```

> **N+1 防护**：必须使用 `Include` 预加载 `Image` 导航属性，避免逐块查询图片。

## 错误码清单

| 错误码 | HTTP | 说明 |
|--------|------|------|
| `DOCLIBRARY_PARSE_NOT_FOUND` | 404 | parse 记录不存在 |
| `DOCLIBRARY_PARSE_NOT_PARSED` | 422 | parse 状态不是 `parsed` |
| `DOCLIBRARY_IMAGE_NOT_FOUND` | 404 | 图片记录不存在 |
| `DOCLIBRARY_OSS_DOWNLOAD_FAILED` | 500 | OSS 下载失败 |
| `DOCLIBRARY_IMPORT_STATUS_INVALID` | 400 | 导入状态参数非法（status 非 imported/failed，或 importedBy 缺失/非 UUID） |
| `DOCLIBRARY_PARSE_ALREADY_IMPORTED` | 422 | parse 已是 `imported` 状态，再次标记 `imported` 被拒绝 |

## 非功能需求

| 编号 | 类别 | 要求 |
|------|------|------|
| NFR-01 | 性能 | 可导入列表查询在 1000 条 parse 记录以内应 ≤ 500ms |
| NFR-02 | 性能 | 结构化块查询在单 parse 500 块以内应 ≤ 500ms（含 `Include(Image)` 预加载） |
| NFR-03 | 性能 | 图片访问首字节响应 ≤ 1s（OSS 下载延迟） |
| NFR-04 | 数据量 | 可导入列表单页最大 100 条；结构化块单页最大 200 条 |
| NFR-05 | 兼容性 | 遵循 DocLibrary 统一响应封装 `{success, data, total, page, pageSize, totalPages}` |
| NFR-06 | 安全性 | 所有端点 `AllowAnonymous`，由部署层网络隔离实现访问控制（内网管理后台） |
| NFR-07 | 可观测性 | 关键操作（导入状态回写、图片下载失败）记录日志；列表查询无额外日志 |
| NFR-08 | 并发 | `document_parse_imports` 表 `parse_id` UNIQUE 约束作为数据库兜底；代码采用"先查后写"模式（`GetByParseIdAsync` 查询已有记录后决定 INSERT / UPDATE），不依赖捕获 UniqueViolation |
| NFR-09 | 数据完整性 | `document_parse_imports.parse_id` ON DELETE CASCADE，parse 删除时联动清除导入记录 |

## 测试策略

| 测试类型 | 范围 | 说明 |
|---------|------|------|
| 单元测试 | `GetImportableListAsync` | 验证分页参数修正、`status=parsed` 过滤、`includeImported` 控制、`search` 模糊匹配 |
| 单元测试 | `GetBlocksAsync` | 验证 parseId/pageId/blockType 过滤、`Include(Image)` 预加载、`blockData` JSON 解析、非法 JSON 兜底 |
| 单元测试 | `UpsertImportStatusAsync` | 验证首次插入、状态覆盖（imported↔failed）、重复 imported 抛异常、parseId 不存在抛异常 |
| 单元测试 | `GetImageBlobAsync` | 验证 imageId 不存在返回 null、OSS 下载异常向上抛 |
| 单元测试 | `blockData` JSON 解析 | 验证合法 JSON、空对象、非法 JSON（返回 null）、null 字符串 |
| 集成测试 | `GET /admin/document-parses/importable` | 验证响应封装、分页字段、排序（无鉴权） |
| 集成测试 | `GET /admin/document-parses/{parseId}/blocks` | 验证 200/404/422 三种响应、pageId=0 不被当作未传参 |
| 集成测试 | `GET /admin/document-parses/images/{imageId}` | 验证 200（正确 Content-Type）/ 404 / 500（OSS 失败） |
| 集成测试 | `POST /admin/document-parses/{parseId}/import-status` | 验证首次 200 / 重复 imported 422 / 状态覆盖 200 / 参数非法 400 / parseId 不存在 404 |
| 集成测试 | 并发写入 | 并发调用 POST import-status 同一 parseId，验证 UNIQUE 约束生效、最终仅一条记录 |
| 边界测试 | 空数据 | parse 无 blocks、parse 无 images、无导入记录 |
| 边界测试 | 数据不一致 | block_type=image 但 image_id=null、OSS 文件被删除 |
