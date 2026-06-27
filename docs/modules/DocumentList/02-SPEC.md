# 文档列表查询与筛选 — 详细需求规格 (SPEC)

## 功能概述和用户故事

**核心用户故事**：作为管理员，我需要查看文档列表并按状态、学科、年级、年份、关键词等条件筛选，以便快速定位目标文档并了解其导入状态。

**补充约束**：
- 本功能仅面向管理后台（`/admin/documents`），不涉及前台用户访问。
- 文档状态查询（`/admin/documents/{id}/status`）作为列表的补充能力，允许管理员查看单篇文档的详细导入任务信息。
- 所有筛选参数均为可选，支持任意组合。

## 功能要求清单（编号 FR-01 起）

| 编号 | 功能要求 | 对应代码方法/端点 |
|------|---------|------------------|
| FR-01 | 分页查询文档列表，支持 page 和 pageSize 参数，返回文档条目及总数 | `GetDocumentListAsync(page, size)` → `GET /admin/documents` |
| FR-02 | 按文档状态（status）筛选文档列表 | `GetDocumentListAsync(..., status)` → `GET /admin/documents?status=` |
| FR-03 | 按学科（subject）筛选文档列表，学科仅支持"英语" | `GetDocumentListAsync(..., subject)` → `GET /admin/documents?subject=` |
| FR-04 | 按年级（grade）筛选文档列表，年级有效值为 K、G1–G12 | `GetDocumentListAsync(..., grade)` → `GET /admin/documents?grade=` |
| FR-05 | 按关键词（keyword）搜索文档列表 | `GetDocumentListAsync(..., keyword)` → `GET /admin/documents?keyword=` |
| FR-06 | 按年份（year）筛选文档列表 | `GetDocumentListAsync(..., year)` → `GET /admin/documents?year=` |
| FR-07 | 根据 ID 获取单篇文档信息 | `GetDocumentAsync(Guid id)` |
| FR-08 | 根据标题获取单篇文档信息 | `GetDocumentByTitleAsync(string title)` |
| FR-09 | 查询单篇文档的状态及关联导入任务信息 | `GetDocumentAsync` + `GetIngestionJobAsync` → `GET /admin/documents/{id}/status` |

## 详细的验收标准（编号 AC-FR-01 起）

### AC-FR-01：分页查询文档列表

**Given** 系统中存在若干文档记录
**When** 管理员调用 `GET /admin/documents?page=1&pageSize=20`
**Then** 返回 HTTP 200，响应体包含 `success: true`，`data` 为当前页文档数组，`total` 为总记录数，`page` 为当前页码，`pageSize` 为每页条数，`totalPages` 为总页数

**Given** 管理员未提供 page 或 pageSize 参数
**When** 调用 `GET /admin/documents`（无查询参数）
**Then** 使用默认值 page=1、pageSize=20 返回结果

**Given** 管理员传入 page ≤ 0
**When** 调用 `GET /admin/documents?page=0`
**Then** page 自动修正为 1

**Given** 管理员传入 pageSize ≤ 0
**When** 调用 `GET /admin/documents?pageSize=0`
**Then** pageSize 自动修正为 20

**Given** 管理员传入 pageSize > 100
**When** 调用 `GET /admin/documents?pageSize=200`
**Then** pageSize 自动修正为 100

**Given** 管理员传入 pageSize=50 且系统有 120 条记录
**When** 调用 `GET /admin/documents?page=1&pageSize=50`
**Then** `totalPages` = (120 + 50 - 1) / 50 = 3

### AC-FR-02：按状态筛选

**Given** 系统中存在不同状态的文档（pending / processing / ready / failed）
**When** 管理员调用 `GET /admin/documents?status=ready`
**Then** 仅返回 `status` 为 `"ready"` 的文档

**Given** 管理员不传 status 参数
**When** 调用 `GET /admin/documents`
**Then** 不按状态筛选，返回所有状态的文档

**Given** 管理员传入无效的 status 值 [推断]
**When** 调用 `GET /admin/documents?status=invalid_status`
**Then** 返回空列表（由仓储层过滤，不报错）[推断]

### AC-FR-03：按学科筛选

**Given** 系统中存在学科为"英语"的文档
**When** 管理员调用 `GET /admin/documents?subject=英语`
**Then** 仅返回 `subject` 为 `"英语"` 的文档

**Given** 管理员传入不支持的学科值
**When** 调用 `GET /admin/documents?subject=数学`
**Then** 返回空列表（列表查询不进行学科校验，由仓储层过滤）[推断]

> 注：学科校验仅在创建/更新文档时执行（`DocLibraryConstants.IsValidSubject`），列表查询不做参数校验 [推断]

### AC-FR-04：按年级筛选

**Given** 系统中存在年级为 "G1" 的文档
**When** 管理员调用 `GET /admin/documents?grade=G1`
**Then** 仅返回 `grade` 为 `"G1"` 的文档

**Given** 管理员传入不支持的年级值
**When** 调用 `GET /admin/documents?grade=G13`
**Then** 返回空列表（列表查询不进行年级校验，由仓储层过滤）[推断]

> 注：年级有效值为 `K`, `G1`, `G2`, `G3`, `G4`, `G5`, `G6`, `G7`, `G8`, `G9`, `G10`, `G11`, `G12`（定义于 `DocLibraryConstants.ValidGrades`）

### AC-FR-05：按关键词搜索

**Given** 系统中存在标题包含"期末"的文档
**When** 管理员调用 `GET /admin/documents?keyword=期末`
**Then** 返回标题匹配关键词的文档 [推断]

**Given** 管理员不传 keyword 参数
**When** 调用 `GET /admin/documents`
**Then** 不按关键词筛选

### AC-FR-06：按年份筛选

**Given** 系统中存在年份为 "2024" 的文档
**When** 管理员调用 `GET /admin/documents?year=2024`
**Then** 仅返回 `year` 为 `"2024"` 的文档

**Given** 管理员不传 year 参数
**When** 调用 `GET /admin/documents`
**Then** 不按年份筛选

### AC-FR-07：根据 ID 获取单篇文档

**Given** 系统中存在指定 ID 的文档
**When** 调用 `GetDocumentAsync(Guid id)`
**Then** 返回对应的 `DocumentModel` 对象

**Given** 系统中不存在指定 ID 的文档
**When** 调用 `GetDocumentAsync(Guid id)`
**Then** 返回 `null`

### AC-FR-08：根据标题获取单篇文档

**Given** 系统中存在指定标题的文档
**When** 调用 `GetDocumentByTitleAsync(string title)`
**Then** 返回对应的 `DocumentModel` 对象

**Given** 系统中不存在指定标题的文档
**When** 调用 `GetDocumentByTitleAsync(string title)`
**Then** 返回 `null`

### AC-FR-09：查询文档状态及导入任务信息

**Given** 系统中存在指定 ID 的文档，且该文档有关联的导入任务
**When** 管理员调用 `GET /admin/documents/{id}/status`
**Then** 返回 HTTP 200，响应体包含：
```json
{
  "success": true,
  "data": {
    "documentId": "...",
    "title": "...",
    "status": "ready",
    "jobs": [
      {
        "jobId": "...",
        "status": "success",
        "parserVersion": "...",
        "ocrVersion": "...",
        "errorMessage": null,
        "startedAt": "2024-01-01T00:00:00.000Z",
        "finishedAt": "2024-01-01T00:01:00.000Z"
      }
    ]
  }
}
```

**Given** 系统中存在指定 ID 的文档，但该文档无关联的导入任务
**When** 管理员调用 `GET /admin/documents/{id}/status`
**Then** 返回 HTTP 200，`jobs` 为空数组 `[]`

**Given** 系统中不存在指定 ID 的文档
**When** 管理员调用 `GET /admin/documents/{id}/status`
**Then** 返回 HTTP 404，响应体为：
```json
{
  "success": false,
  "message": "文档不存在",
  "errorCode": "DOCLIBRARY_DOCUMENT_NOT_FOUND"
}
```

## 非功能需求

| 编号 | 类别 | 要求 |
|------|------|------|
| NFR-01 | 性能 | 文档列表查询响应时间在 1000 条记录以内应 ≤ 500ms [推断] |
| NFR-02 | 数据量 | 单页最大返回 100 条记录（`size > 100` 时自动修正为 100） |
| NFR-03 | 兼容性 | API 遵循 RESTful 风格，返回 JSON 格式，时间字段使用 ISO 8601 UTC 格式（`yyyy-MM-dd'T'HH:mm:ss.fff'Z'`） |
| NFR-04 | 安全性 | 本功能仅限管理后台路径 `/admin/*` 访问 [推断] |
| NFR-05 | 可观测性 | 关键操作（文档创建、删除、元数据更新）已记录日志；列表查询本身无额外日志输出 |

## 测试策略

| 测试类型 | 范围 | 说明 |
|---------|------|------|
| 单元测试 | `DocumentDomainService.GetDocumentListAsync` | 验证分页参数修正逻辑（page ≤ 0 → 1, size ≤ 0 → 20, size > 100 → 100） |
| 单元测试 | `DocumentDomainService.GetDocumentAsync` | 验证存在/不存在 ID 的返回值 |
| 单元测试 | `DocumentDomainService.GetDocumentByTitleAsync` | 验证存在/不存在标题的返回值 |
| 单元测试 | `DocumentDomainService.GetIngestionJobAsync` | 验证存在/不存在文档 ID 时导入任务的返回值 |
| 集成测试 | `GET /admin/documents` | 验证各筛选参数组合的正确性，包括无参数默认分页、单条件筛选、多条件组合筛选 |
| 集成测试 | `GET /admin/documents/{id}/status` | 验证文档存在时返回状态与任务信息、文档不存在时返回 404 及 `DOCLIBRARY_DOCUMENT_NOT_FOUND` |
| 集成测试 | 分页边界 | 验证空列表、仅 1 条记录、跨页等边界场景 |
| 集成测试 | 响应格式 | 验证返回字段完整性和时间格式（ISO 8601 UTC） |
