# 01-FEATURE — Document Parse

## 功能名称

**Document Parse** — MinerU 文档解析与解析记录管理

## 功能概述

本模块实现文档的 **MinerU Precision API 异步解析** 以及 **解析记录的查询/删除管理**。

1. **解析触发**：管理员对已上传的文档文件调用 `POST /admin/document-files/{id}/parse`，系统创建一条 `document_parses` 记录（`status="pending"`），MinerU 异步解析由后台 Worker 接管。
2. **后台异步解析**：`MinerUFileParseWorker`（`BackgroundService`，每 5 秒轮询）拉取 `pending` 任务，依次执行"格式转换 → 页数检测 → 提交 MinerU → 轮询状态 → 下载 ZIP → 持久化"全流程。解析状态全部持久化在数据库中，前端无状态、刷新安全。
3. **解析记录管理**：`GET /admin/document-parses` 分页列出所有解析记录（含按文件名模糊搜索），`DELETE /admin/document-parses/{parseId}` 删除单条解析记录并联动物理图片、数据库记录、OpenSearch 索引的清理。

支持的文档格式：PDF、DOC、DOCX、PPT、PPTX（≤200MB）。非 PDF 文件先由 doc-converter 基础服务（HTTP）转为 PDF；超过 200 页的大文档由 PdfSplitService 拆分为多个子文档分别解析后合并结果。

## 背景

文档上传（`document_files` 表）与文档解析（`document_parses` 表）解耦：文件可独立存在，解析为可选操作。同一文件可同时拥有 `vlm` 和 `pipeline` 两种模型版本的解析结果，通过 `model_version` 字段区分。

MinerU Precision API 的解析耗时从数十秒到数十分钟不等（取决于文档页数与 API 队列），因此整个解析流程必须异步化，由数据库状态机驱动。

## 用户故事

- **作为管理员**：我上传文档后点击"解析"，系统在后台自动完成 Markdown 提取、图片上传、结构化 block 生成，我可以在解析完成后查看结果
- **作为管理员**：我可以在"解析结果"页面查看所有解析记录、按文件名搜索、删除不再需要的解析记录（自动清理关联的图片和索引）
- **作为运维**：MinerU API 不可用时，解析记录标记为 `failed`，不影响其他文件的解析，稍后可通过重新触发解析来恢复
- **作为运维**：大文档（>200 页）自动分块解析、非 PDF 文档自动转档，无需人工预处理

## 功能需求

### FR-01:解析触发
- `POST /admin/document-files/{id}/parse?modelVersion=vlm|pipeline`
- 文件存在性校验、同 modelVersion 进行中解析校验 (`pending`/`parsing` → 422)、MinerU Token 配置校验
- 新建 `document_parses` 记录，`status="pending"`，由 Worker 异步接管

### FR-02:MinerU 后台解析 Worker
- `MinerUFileParseWorker`（`BackgroundService`）轮询 `pending` 解析任务
- 非 PDF 文件通过 `IFileConversionService`（HTTP 调用 doc-converter 基础服务）转为 PDF
- ≤200 页：直接提交 MinerU；>200 页：`IPdfSplitService` 拆分为多个子文档逐块提交后合并
- 持久化：ZIP 上传 OSS、图片上传 OSS 并替换 Markdown 路径、blocks 入库、状态更新为 `parsed`
- 解析完成后的 best-effort 后置步骤：OpenSearch 索引 blocks、LLM 分析元数据

### FR-03:MinerU Precision API 客户端
- `MinerUPrecisionClient`（Singleton）：presigned URL 方式提交（文件已在 OSS）、Bearer Token 认证
- 三步流程：`SubmitUrlAsync` → `PollStatusAsync` → `DownloadAndProcessZipAsync`
- ZIP 解析：`full.md`（必需）、`content_list.json`、`content_list_v2.json`、`model.json`、`layout.json`、`images/` 目录
- 图片上传 OSS（路径 `mineru/{taskId}/`），Markdown 中的相对路径替换为完整 OSS 路径

### FR-04:转档服务
- `IFileConversionService` / `RemoteFileConversionService`（通过 `AddHttpClient` 注册）：DOC/DOCX/PPT/PPTX → PDF
- HTTP 调用独立的 doc-converter 基础服务（`POST /convert`，multipart/form-data）
- `IsAvailable` 恒为 `true`（HTTP 可用性在首次调用时惰性检测）；转档失败（超时/HTTP 错误/非 2xx）返回 `null`，解析标记 `failed`
- HTTP 超时硬编码 180s，无需配置

### FR-05:PDF 分页服务
- `IPdfSplitService` / `PdfSplitService`（Singleton，基于 PdfSharpCore）
- `GetPageCount` 读取 PDF 页数；`SplitPdf` 按指定页数上限拆分为多个子 PDF 流

### FR-06:解析状态机
- `pending` → `parsing` → `parsed` / `failed`
- `failed` 可通过新建解析记录重试（保留历史）
- `DocumentParseService.UpdateStatusAsync` 更新状态；`Parsed` 时自动设置 `ParsedAt`

### FR-07:解析记录列表
- `GET /admin/document-parses?page=&pageSize=&search=`
- 返回解析记录列表（含关联文件名），按 `ParsedAt DESC` 排序，`search` 按文件名模糊匹配

### FR-08:删除解析记录联动
- `DELETE /admin/document-parses/{parseId}`
- 删除 OSS 图片（best-effort）→ 删除 DB 记录（级联 blocks/images）→ 删除 OpenSearch 索引（best-effort）
- 不影响原始文档文件

### FR-09:Block 持久化
- `IDocumentParseBlockService.InsertBlocksFromContentListAsync`：解析 `content_list.json`，覆盖式写入 blocks
- 文本内容提取优先级：`text` → `content` → `body`
- image 类型 block 通过 `img_path` 关联图片 ID

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | 对已上传文件触发解析（vlm 或 pipeline），新建 `pending` 状态解析记录 |
| AC-02 | 同文件同 modelVersion 已有 `pending`/`parsing` 解析时，再次触发返回 422 `DOCLIBRARY_PARSE_IN_PROGRESS` |
| AC-03 | MinerU Token 未配置时返回 503 `DOCLIBRARY_MINERU_NOT_CONFIGURED` |
| AC-04 | Worker 完成解析后，`status="parsed"`，`markdown_content` 非空，`document_parse_images` 有记录，blocks 入库 |
| AC-05 | 非 PDF 文件自动通过 doc-converter 转 PDF 后解析；转档失败时标记 `failed` |
| AC-06 | >200 页 PDF 自动分块解析并合并结果，存为一条 parse 记录 |
| AC-07 | 解析失败（MinerU 返回失败/超时）时 `status="failed"`，`error_message` 非空 |
| AC-08 | `GET /admin/document-parses` 分页返回解析记录，支持 `search` 按文件名过滤 |
| AC-09 | `DELETE /admin/document-parses/{parseId}` 删除解析记录、OSS 图片、OpenSearch 索引，不影响原始文件 |
| AC-10 | MinerU API 不可用时，已完成解析不受影响，仅失败任务标记 `failed` |
| AC-11 | 同一文件可同时拥有 vlm 和 pipeline 两条 `parsed` 解析记录 |
| AC-12 | 解析完成后自动索引 OpenSearch、自动分析 LLM 元数据（均为 best-effort，失败不阻塞） |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | 解析全流程异步化，HTTP 请求仅创建记录，不阻塞等待解析完成 |
| NFR-02 | 所有解析状态持久化在数据库，前端刷新不丢失进度 |
| NFR-03 | 并发安全：Worker 可处理多个 `pending` 任务（逐个处理，单任务异常不影响其他） |
| NFR-04 | OpenSearch 索引 / LLM 元数据分析失败不阻塞解析主流程（best-effort） |
| NFR-05 | 图片上传到 OSS 路径 `mineru/{taskId}/{imageName}`，MD 中存储完整 OSS 路径 |
| NFR-06 | presigned URL 有效期 3600 秒（1 小时） |
| NFR-07 | MinerU 单次解析超时 30 分钟 |

## 数据来源

- **解析任务驱动**：`document_parses` 表中 `status="pending"` 的记录
- **解析结果存储**：`document_parses`（Markdown/json 字段）、`document_parse_images`（图片）、`document_parse_blocks`（结构化块）
- **外部 API**：MinerU Precision API（`/api/v4/extract/task`、`/api/v4/extract/task/{taskId}`）
- **配置**：`MinerU` 配置段（`ApiToken` / `BaseUrl` / `ModelVersion`）

## 接口清单

| 组件 | 变更 |
|------|------|
| `MinerUFileParseWorker` | 新增 BackgroundService（轮询 + 解析流程编排） |
| `MinerUPrecisionClient` | 新增 Singleton（提交/轮询/下载 ZIP） |
| `IFileConversionService` / `RemoteFileConversionService` | 新增（HTTP 调用 doc-converter，非 PDF → PDF） |
| `IPdfSplitService` / `PdfSplitService` | 新增 Singleton（页数检测/拆分） |
| `DocumentParseEndpoints` | 新增 `/admin/document-parses` 端点组（列表/删除） |
| `IDocumentParseService` | 新增解析服务接口（CRUD + 状态管理 + 列表/删除） |
| `IDocumentParseBlockService` | 新增 block 服务接口（content_list.json → blocks） |
| `DocumentParseEntity` | 新增 `model_version` / `content_list_v2` / `model_json` / `layout_json` 列；移除 `layout_pdf_path` |
| `DocumentParseBlockEntity` / `DocumentParseImageEntity` | 新增 block / image 实体 |
