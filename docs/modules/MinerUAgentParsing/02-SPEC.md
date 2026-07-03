# MinerU 文档解析（持久化版）— 详细需求规格 (SPEC)

## 功能概述

本模块实现文件管理与文档解析一体化流程：文件上传存储 → 触发 MinerU 解析 → Markdown 查看/导出。文件与解析解耦，文件可独立存在，解析为可选操作。所有状态持久化在数据库中，前端无状态，刷新安全。

## 前端菜单结构

| 菜单 | Key | 功能 |
|------|-----|------|
| 文档管理 | documents | 上传文件、搜索文件名、触发解析/重新解析、删除文件（含关联解析和图片） |
| 解析结果 | results | 查看所有解析记录、按文档名搜索、查看解析详情（Markdown/HTML预览/Layout PDF/导出）、删除解析记录 |
| 检索测试 | search | 保留现有功能 |

## 数据模型

### document_files — 文件基础信息

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| file_name | varchar(500) | 原始文件名 |
| file_path | varchar(500) | S3 源文件路径 |
| content_type | varchar(100) | MIME 类型 |
| created_by | uuid | 上传人 |
| created_at | timestamptz | 创建时间（数据库自动维护） |
| updated_at | timestamptz | 更新时间（数据库自动维护） |

### document_parses — 解析记录

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| document_file_id | uuid | FK → document_files（级联删除） |
| model_version | varchar(20) | 解析模型版本：vlm / pipeline |
| status | varchar(30) | pending / parsing / parsed / failed |
| external_task_id | varchar(100) | MinerU 任务 ID |
| markdown_content | text | 解析后的 MD（图片路径为 S3 路径） |
| content_list | jsonb | MinerU 输出的 content_list.json（vlm 和 pipeline 模型均会产出，视 MinerU 版本而定） |
| content_list_v2 | jsonb | MinerU 输出的 content_list_v2.json（vlm 和 pipeline 模型均会产出，视 MinerU 版本而定） |
| model_json | jsonb | MinerU 输出的 model.json — 模型推理结果含 bbox 坐标和版面分类（vlm 和 pipeline 模型均会产出，视 MinerU 版本而定） |
| layout_json | jsonb | MinerU 输出的 layout.json — 版面分析数据含每页 bbox 坐标（vlm 和 pipeline 模型均会产出，视 MinerU 版本而定） |
| zip_path | varchar(500) | 完整 ZIP 包的 OSS 路径 |
| error_message | text | 失败原因 |
| parsed_at | timestamptz | 解析完成时间 |

### document_parse_blocks — 解析内容块

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| parse_id | uuid | FK → document_parses（级联删除） |
| page_id | integer | 页码（从 0 开始） |
| sort_index | integer | 页内排序序号 |
| block_type | varchar(20) | 块类型：text / image / table / equation / title 等 |
| text_content | text | 提取的文本内容 |
| image_id | uuid | FK → document_parse_images（SET NULL） |
| block_data | jsonb | 原始块 JSON 数据（来自 content_list.json） |
| created_at | timestamptz | 创建时间 |

### document_parse_images — 解析产出的图片

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | 主键 |
| parse_id | uuid | FK → document_parses（级联删除） |
| image_name | varchar(200) | 图片文件名 |
| image_path | varchar(500) | S3 图片路径 |
| content_type | varchar(50) | MIME 类型 |

## API 规格

### API-1：上传文件

`POST /admin/document-files/upload` (multipart/form-data)

- 请求字段：`file`（必填，PDF/DOCX/PPTX，≤200MB）
- 响应：`{ success, data: { id, fileName, contentType } }`
- 错误码：`DOCLIBRARY_FILE_REQUIRED`、`DOCLIBRARY_FILE_FORMAT_UNSUPPORTED`

### API-2：文件列表

`GET /admin/document-files?page=1&pageSize=20&parseStatus=`

- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- 每项包含：`id, fileName, contentType, createdAt, createdBy, parseStatus, parsedAt`
- `parseStatus` 取自该文件最新 parse 记录的 status，无 parse 记录时为 null
- 查询参数 `parseStatus`（可选）：
  - 不传或空：返回所有文件
  - `unparsed`：返回 parseStatus 为 null 的文件（未解析）
  - `pending` / `parsing` / `parsed` / `failed`：返回对应状态的文件

### API-3：触发解析

`POST /admin/document-files/{id}/parse`

- 请求参数（query）：`modelVersion`（必填，`vlm` 或 `pipeline`）
- 前置条件：文件存在，且该 modelVersion 无进行中的解析
- 同一文件可以同时拥有 vlm 和 pipeline 两种解析结果
- 响应：`{ success, data: { id, parseId, status, modelVersion } }`
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_PARSE_IN_PROGRESS`、`DOCLIBRARY_MINERU_NOT_CONFIGURED`

### API-4：获取文件详情（含解析列表）

`GET /admin/document-files/{id}`

- 响应：`{ success, data: { id, fileName, contentType, createdAt, parses: [...] } }`
- `parses` 数组包含该文件所有解析记录，每项：`{ id, modelVersion, status, markdownContent, contentList, contentListV2, modelJson, layoutJson, errorMessage, parsedAt, images: [...] }`
- `markdownContent` 中的图片路径已替换为 S3 presigned URL
- `contentList` / `contentListV2` / `modelJson` / `layoutJson`：vlm 和 pipeline 模式均可能有值（视 MinerU 版本和 ZIP 内容而定，代码按模式无关方式存储所有找到的字段）
- `images` 数组包含每张图片的 `id, imageName, imageUrl`（presigned URL）
- 无解析记录时 `parses` 为空数组

### API-5：删除文件

`DELETE /admin/document-files/{id}`

- 同时删除 S3 上的源文件和所有关联解析的图片
- 级联删除 document_parses 和 document_parse_images 记录
- 响应：`{ success, data: { id, deleted } }`

### API-6：按文件 ID 导出为 MD+图片 ZIP

`GET /admin/document-files/{id}/export/markdown`

- 前置条件：文件存在且最新 parse status=parsed
- 响应：`application/zip` 二进制流，文件名 `{fileName}_markdown.zip`
- ZIP 内包含：
  - `{fileName}.md` — Markdown 文件，图片引用为相对路径 `images/{imageName}`
  - `images/` 目录 — 所有引用的图片文件
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_FILE_NOT_PARSED`

### API-7：按文件 ID 导出为 HTML

`GET /admin/document-files/{id}/export/html`

- 前置条件：文件存在且最新 parse status=parsed
- 响应：`text/html` 二进制流，文件名 `{fileName}.html`
- HTML 为自包含文件：图片以 base64 data URI 内嵌，CSS 内联
- 错误码：`DOCLIBRARY_FILE_NOT_FOUND`、`DOCLIBRARY_FILE_NOT_PARSED`

### API-8：解析记录列表

`GET /admin/document-parses?page=1&pageSize=20&search=`

- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- 每项包含：`id, fileId, fileName, modelVersion, status, parsedAt, errorMessage`
- `fileName` 来自关联的 document_files.file_name
- 查询参数 `search`（可选）：按文档名模糊匹配
- 按 parsed_at DESC 排序（最新解析在前）

### API-9：删除解析记录

`DELETE /admin/document-parses/{parseId}`

- 仅删除指定的解析记录和关联的 S3 图片
- **不删除**原始文件（document_files 记录保留）
- 级联删除 document_parse_images 记录
- 响应：`{ success, data: { id, deleted } }`
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`

### API-10：按解析 ID 导出为 MD+图片 ZIP

`GET /admin/document-parses/{parseId}/export/markdown`

- 前置条件：解析记录存在且 status=parsed
- 响应：`application/zip` 二进制流，文件名 `{fileName}_markdown.zip`
- ZIP 内包含：
  - `{fileName}.md` — Markdown 文件，图片引用为相对路径 `images/{imageName}`
  - `images/` 目录 — 所有引用的图片文件
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`、`DOCLIBRARY_PARSE_NOT_PARSED`

### API-11：按解析 ID 导出为 HTML

`GET /admin/document-parses/{parseId}/export/html`

- 前置条件：解析记录存在且 status=parsed
- 响应：`text/html` 二进制流，文件名 `{fileName}.html`
- HTML 为自包含文件：图片以 base64 data URI 内嵌，CSS 内联
- 错误码：`DOCLIBRARY_PARSE_NOT_FOUND`、`DOCLIBRARY_PARSE_NOT_PARSED`

## 详细验收标准

### AC-UPLOAD-01：正常上传

- **Given** 合法 PDF 文件（≤200MB）
- **When** 调用 `POST /admin/document-files/upload`
- **Then** 返回 200，S3 中存在文件，document_files 有记录，无 parse 记录

### AC-UPLOAD-02：文件校验

- **Given** 文件为空或格式不支持
- **When** 调用上传 API
- **Then** 返回 400，对应错误码

### AC-UPLOAD-03：文件超限

- **Given** 文件 > 200MB
- **When** 调用上传 API
- **Then** 返回 400

### AC-PARSE-01：正常解析

- **Given** 文件存在，该 modelVersion 无进行中的解析，MinerU Token 已配置
- **When** 调用 `POST /admin/document-files/{id}/parse?modelVersion=vlm`
- **Then** 新建 document_parses 记录（model_version='vlm'），status=pending，Worker 接管后续流程

### AC-PARSE-01b：Pipeline 解析

- **Given** 同上
- **When** 调用 `POST /admin/document-files/{id}/parse?modelVersion=pipeline`
- **Then** 新建 document_parses 记录（model_version='pipeline'），status=pending

### AC-PARSE-02：解析完成

- **Given** MinerU 解析成功
- **When** Worker 完成 ZIP 下载、图片上传、MD 替换
- **Then** parse status=parsed，markdown_content 非空，document_parse_images 有记录；content_list / content_list_v2 / model_json / layout_json 视 MinerU 版本和 ZIP 内容而定（vlm 和 pipeline 模式均可能产出这些字段）

### AC-PARSE-03：解析失败

- **Given** MinerU 解析失败
- **When** Worker 捕获错误
- **Then** parse status=failed，error_message 非空

### AC-PARSE-04：重复解析（同模型）

- **Given** 文件最新 parse（同 modelVersion）status=failed 或 parsed
- **When** 调用解析 API（同 modelVersion）
- **Then** 新建 parse 记录，status=pending（保留历史记录）

### AC-PARSE-04b：不同模型可同时解析

- **Given** 文件已有 vlm 的 parsed 解析
- **When** 调用解析 API（modelVersion=pipeline）
- **Then** 新建 parse 记录（model_version='pipeline'），与 vlm 记录并存

### AC-PARSE-05：解析进行中（同模型）

- **Given** 文件最新 parse（同 modelVersion）status=pending 或 parsing
- **When** 调用解析 API（同 modelVersion）
- **Then** 返回 422，`DOCLIBRARY_PARSE_IN_PROGRESS`

### AC-PARSE-06：Token 未配置

- **Given** MinerU:ApiToken 为空
- **When** 调用解析 API
- **Then** 返回 503，`DOCLIBRARY_MINERU_NOT_CONFIGURED`

### AC-VIEW-01：查看已解析文件

- **Given** 文件存在，有 parsed 的 parse 记录
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** 返回 markdown_content，图片路径为 presigned URL

### AC-VIEW-02：查看未解析文件

- **Given** 文件存在，无 parse 记录
- **When** 调用 `GET /admin/document-files/{id}`
- **Then** parse 为 null

### AC-DELETE-01：删除文件

- **Given** 文件存在
- **When** 调用 `DELETE /admin/document-files/{id}`
- **Then** 数据库记录级联删除，S3 源文件和关联图片删除

### AC-DELETE-02：删除解析记录

- **Given** 解析记录存在
- **When** 调用 `DELETE /admin/document-parses/{parseId}`
- **Then** 删除解析记录和关联图片（S3 + DB），原始文件保留

### AC-DELETE-03：删除解析记录不影响文件

- **Given** 文件有 2 条解析记录
- **When** 删除其中 1 条
- **Then** 文件和另 1 条解析记录保留

### AC-CONCURRENT-01：并发解析

- **Given** 多个文件同时触发解析
- **When** Worker 处理
- **Then** 每个文件独立处理，互不影响

### AC-EXPORT-01：按文件 ID 导出 MD+图片 ZIP

- **Given** 文件最新 parse status=parsed
- **When** 调用 `GET /admin/document-files/{id}/export/markdown`
- **Then** 返回 ZIP 文件，包含 `.md` 文件和 `images/` 目录

### AC-EXPORT-02：按文件 ID 导出 HTML

- **Given** 文件最新 parse status=parsed
- **When** 调用 `GET /admin/document-files/{id}/export/html`
- **Then** 返回自包含 HTML 文件，图片以 base64 data URI 内嵌

### AC-EXPORT-03：导出未解析文件

- **Given** 文件无 parsed 的 parse 记录
- **When** 调用按文件 ID 的导出 API
- **Then** 返回 422，`DOCLIBRARY_FILE_NOT_PARSED`

### AC-EXPORT-04：导出不存在的文件

- **Given** 文件 ID 不存在
- **When** 调用按文件 ID 的导出 API
- **Then** 返回 404，`DOCLIBRARY_FILE_NOT_FOUND`

### AC-EXPORT-05：按解析 ID 导出 MD+图片 ZIP

- **Given** 解析记录存在且 status=parsed
- **When** 调用 `GET /admin/document-parses/{parseId}/export/markdown`
- **Then** 返回 ZIP 文件，包含 `.md` 文件和 `images/` 目录

### AC-EXPORT-06：按解析 ID 导出 HTML

- **Given** 解析记录存在且 status=parsed
- **When** 调用 `GET /admin/document-parses/{parseId}/export/html`
- **Then** 返回自包含 HTML 文件，图片以 base64 data URI 内嵌

### AC-EXPORT-07：按解析 ID 导出未解析记录

- **Given** 解析记录 status 不为 parsed
- **When** 调用按解析 ID 的导出 API
- **Then** 返回 422，`DOCLIBRARY_PARSE_NOT_PARSED`

### AC-PREVIEW-01：在线预览解析记录

- **Given** 解析记录存在且 status=parsed
- **When** 点击"预览"按钮
- **Then** 在新浏览器 tab 中打开自包含 HTML 页面，展示解析后的文档内容

### AC-LAYOUT-01：查看 Layout JSON

- **Given** 文件有解析记录（status=parsed，vlm 或 pipeline 均可），且 layout_json 非空
- **When** 前端展示该解析记录
- **Then** 显示 Layout 按钮，点击可在新窗口查看格式化的 layout.json 数据

### AC-LAYOUT-02：解析记录无 Layout/Model 数据

- **Given** 文件有解析记录（status=parsed，vlm 或 pipeline 均可），但 layout_json / model_json / content_list_v2 为空
- **When** 前端展示该解析记录
- **Then** 对应字段无值时，点击按钮提示暂无数据（按钮始终显示，不按 modelVersion 过滤）

### AC-LAYOUT-03：解析记录有 Layout JSON 但点击查看

- **Given** 文件有解析记录（status=parsed），layout_json 非空
- **When** 点击 Layout 按钮
- **Then** 在新窗口查看格式化的 layout.json 数据

### AC-PREVIEW-02：预览未解析记录

- **Given** 解析记录 status 不为 parsed
- **When** 点击"预览"按钮
- **Then** 按钮不可用（disabled）

### AC-LIST-01：文件列表过滤

- **Given** 系统有 5 个文件，3 个未解析，2 个已解析
- **When** 调用 `GET /admin/document-files?parseStatus=unparsed`
- **Then** 返回 3 个未解析文件

### AC-LIST-02：解析记录列表

- **Given** 系统有 3 条解析记录
- **When** 调用 `GET /admin/document-parses`
- **Then** 返回 3 条记录，每条包含 fileName, status, parsedAt

### AC-LIST-03：解析记录搜索

- **Given** 有解析记录关联文件名为 "英语三年级.pdf" 和 "数学五年级.pdf"
- **When** 调用 `GET /admin/document-parses?search=英语`
- **Then** 仅返回 "英语三年级.pdf" 的解析记录

### AC-SPLIT-01：大文档自动拆分解析

- **Given** 上传的 PDF 超过 200 页
- **When** Worker 处理该文件的解析任务
- **Then** 自动将 PDF 按每 200 页拆分为多个子文档，分别提交 MinerU 解析

### AC-SPLIT-02：拆分后合并结果

- **Given** 大文档被拆分为 N 个子文档，全部解析完成
- **When** Worker 合并结果
- **Then** 所有子文档的 Markdown 按顺序拼接为一份完整 Markdown，图片统一管理，存为一条 parse 记录

### AC-SPLIT-03：拆分后部分失败

- **Given** 大文档被拆分为 3 个子文档，其中 1 个解析失败
- **When** Worker 检测到失败
- **Then** 整个 parse 标记为 failed，error_message 包含失败的子任务信息；已成功的子任务图片保留在 S3

### AC-SPLIT-04：200 页以内的文档

- **Given** 上传的 PDF 为 150 页
- **When** Worker 处理该文件的解析任务
- **Then** 不拆分，直接提交 MinerU 解析（与现有流程一致）

### AC-CONVERT-01：非 PDF 文件自动转 PDF

- **Given** 上传的文件为 DOCX/PPTX 格式
- **When** Worker 处理该文件的解析任务
- **Then** 先通过 LibreOffice headless 将文件转换为 PDF，再按 PDF 流程处理（检测页数→拆分→提交解析）

### AC-CONVERT-02：PDF 文件不转换

- **Given** 上传的文件为 PDF 格式
- **When** Worker 处理该文件的解析任务
- **Then** 直接进入页数检测流程，不做格式转换

### AC-CONVERT-03：转换失败

- **Given** 上传的 DOCX 文件损坏或格式不支持
- **When** LibreOffice 转换失败
- **Then** parse 标记为 failed，error_message 包含转换失败原因

### AC-CONVERT-04：LibreOffice 未安装

- **Given** 服务器未安装 LibreOffice
- **When** Worker 尝试转换非 PDF 文件
- **Then** parse 标记为 failed，error_message 提示 LibreOffice 未配置

## 解析状态流转

```
触发解析 ──▶ pending ──Worker开始──▶ parsing ──成功──▶ parsed
                                      │
                                      │ 失败
                                      ▼
                                    failed (可重试，新建记录)
```

## 非功能需求

- **刷新安全**：所有状态在数据库，前端刷新不影响
- **并发安全**：多个文件可同时解析
- **文件与解析解耦**：文件可独立存在，解析为可选操作；未来可支持其他解析方式
- **图片管理**：图片上传到 S3 的 `documents/mineru/{taskId}/{imageName}` 路径，MD 中存储 S3 路径，查看时替换为 presigned URL
- **Presigned URL 有效期**：1 小时

## MinerU ZIP 解析逻辑

MinerU API 返回的 ZIP 包含以下文件：

### Pipeline 模式输出
- `full.md` — 完整 Markdown 内容（必需）
- `images/` 目录 — 所有提取的图片
- `{uuid}_content_list.json` — 结构化内容块 v1（含文本/图片/表格/公式块）
- `{uuid}_content_list_v2.json` — 结构化内容块 v2（增强版，字段更丰富）
- `{uuid}_model.json` — 模型推理结果（含 bbox 坐标、版面分类、置信度）
- `layout.json` — 版面分析数据（含每页所有 bbox 坐标和分类）
- `{uuid}_origin.pdf` — 原始 PDF 副本

### VLM 模式输出
- `full.md` — 完整 Markdown 内容
- `images/` 目录 — 提取的图片
- 视 MinerU 版本而定，可能包含与 pipeline 模式相同的完整结构化数据：
  - `{uuid}_content_list.json` — 结构化内容块 v1
  - `{uuid}_content_list_v2.json` — 结构化内容块 v2
  - `{uuid}_model.json` — 模型推理结果
  - `layout.json` — 版面分析数据
- 注：当前 MinerU API 升级后，vlm 模式通常也会返回 content_list_v2 / model.json / layout.json，代码按模式无关的方式存储所有找到的字段

### ZIP 条目匹配规则
1. **full.md**：精确匹配根目录的 `full.md`（找不到则抛异常）
2. **content_list.json**：按优先级依次尝试：
   - 精确匹配 `content_list.json`
   - 匹配任何以 `_content_list.json` 结尾的路径（如 `{uuid}_content_list.json`）
   - 匹配任何以 `/content_list.json` 结尾的路径
   - 以上均未找到时默认返回 `"[]"`
3. **content_list_v2.json**：按优先级依次尝试：
   - 精确匹配 `content_list_v2.json`
   - 匹配任何以 `_content_list_v2.json` 结尾的路径
   - 匹配任何以 `/content_list_v2.json` 结尾的路径
   - 以上均未找到时默认返回 null
4. **model.json**：按优先级依次尝试：
   - 匹配任何以 `_model.json` 结尾的路径（如 `{uuid}_model.json`）
   - 精确匹配 `model.json`
   - 以上均未找到时默认返回 null
5. **layout.json**：按优先级依次尝试：
   - 精确匹配 `layout.json`
   - 匹配任何以 `_layout.json` 结尾的路径
   - 匹配任何以 `/layout.json` 结尾的路径
   - 以上均未找到时默认返回 null
6. **图片**：匹配所有以 `images/` 开头且长度 > 0 的条目

### 调试日志
- 每次下载 ZIP 后会输出完整条目列表：`ZIP entries for task {TaskId}: {Entries}`
- 各 JSON 文件找到/未找到均输出相应级别日志
