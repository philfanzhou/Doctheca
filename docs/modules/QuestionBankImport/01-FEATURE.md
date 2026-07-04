# 01-FEATURE — QuestionBank 拉模式导入接口

## 功能名称

**QuestionBank Import (Pull Mode)** — 题库服务拉取解析产出接口

## 功能概述

本模块向 QuestionBank 服务暴露 DocLibrary 已持久化的 MinerU 解析产出，使 QuestionBank 能够以"拉模式"获取试卷解析数据，自行完成题号识别、题型分类、边界判定并入库。

按已定架构决策（2026-07-04 讨论），DocLibrary 与 QuestionBank 保持服务分立；DocLibrary 不负责拆题，仅作为"教学资料中心"提供结构化解析数据；QuestionBank 主动拉取并自行拆题，以保证：

- DocLibrary 职责单一（文档解析与存储）
- QuestionBank 拥有题库领域完整逻辑（拆题/分类/检索）
- 数据一致性由 QuestionBank 单点写入保证
- 后续可从手动触发演进到定时/事件驱动，不影响 DocLibrary

### 拉模式数据流

```
DocLibrary                      QuestionBank
───────────                     ────────────
document_files ──┐
document_parses ─┼─► 4 类 HTTP 接口 ──► 拉取客户端 ──► 拆题入库
parse_blocks ────┤   (JWT 鉴权)                      (PostgreSQL GIN 索引)
parse_images ────┘
parse_imports ◄── POST /import-status (回写导入状态)
```

## 单一用户故事

> **作为** QuestionBank 服务，
> **我希望** 通过 HTTP API 拉取 DocLibrary 已完成的 MinerU 解析产出（文档列表、结构化块、图片、原 JSON），并能回写导入状态以避免重复处理，
> **以便** 我可以自主完成试卷到题库的拆分入库，DocLibrary 无需感知题库领域逻辑。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | QuestionBank 携带有效 JWT 可调用全部 4 类接口；未认证请求返回 401 | 集成测试 + API 测试 |
| AC-2 | `GET /admin/document-parses/importable` 仅返回 `status=parsed` 的记录，按 `parsed_at DESC` 排序，支持分页与文件名模糊搜索 | 单元测试 + 集成测试 |
| AC-3 | 默认排除已有 `imported` 状态的 parse；通过 `includeImported=true` 可包含 | 单元测试 |
| AC-4 | `GET /admin/document-parses/{parseId}/blocks` 返回该 parse 全部 `document_parse_blocks` 行，支持按 `pageId` / `blockType` 过滤与分页 | 单元测试 + 集成测试 |
| AC-5 | 每个 block 返回 `blockData` 字段（解析后的 JSON 对象，非原始字符串），image 类型 block 额外返回 `imageName` / `imagePath` / `imageUrl`(presigned) | 单元测试 |
| AC-6 | `GET /admin/document-parses/images/{imageId}` 返回图片二进制流（`image/jpeg` 等正确 MIME），不存在返回 404 | 集成测试 |
| AC-7 | `POST /admin/document-parses/{parseId}/import-status` 首次写入返回 200；同一 parseId 重复标记 `imported` 返回 422 + `DOCLIBRARY_PARSE_ALREADY_IMPORTED` | 单元测试 + 集成测试 |
| AC-8 | `imported` 状态可被 `failed` 覆盖（允许重试）；`failed` 状态可被 `imported` 覆盖（重试成功） | 单元测试 |
| AC-9 | 接口响应遵循 DocLibrary 统一封装 `{success, data, total, page, pageSize, totalPages}`；失败返回 `{success=false, message, errorCode}` | 集成测试 |
| AC-10 | 所有接口路径以 `/admin/` 开头并强制 `RequireAuthorization()`，与现有端点风格一致 | 代码审查 |

## 范围内

- 4 类 HTTP 接口实现（可导入列表 / 结构化块检索 / 图片访问 / 导入状态回写）
- 新增 `document_parse_imports` 表（记录 QuestionBank 导入状态）
- `DatabaseInitializer.cs` 新增建表 SQL
- 图片访问端点通过 `IOssService.DownloadAsync` 流式返回
- 现有 `IDocumentParseService` 扩展方法（GetImportableListAsync / GetBlocksAsync / UpsertImportStatusAsync）
- 单元测试覆盖核心路径

## 范围外

- QuestionBank 端的拉取客户端实现（由 QuestionBank 项目负责）
- QuestionBank 端拆题入库逻辑（题号识别/题型分类/边界判定）
- QuestionBank PostgreSQL GIN 全文索引（由 QuestionBank 项目负责）
- 定时拉取调度与事件驱动（后续阶段）
- DocLibrary 主动推送（架构已否决，保持拉模式）
- 文档已存在 `document_segments` / `question_segments` 表的改造（属于 IngestionWorker 本地解析链路，与 MinerU 链路无关，不混用）
- 服务间认证机制改造（沿用 JWT Bearer，不做服务账号专设）

## 数据模型

### 新增表：`document_parse_imports`

记录 QuestionBank 对某个 parse 的导入状态，用于防重复处理与状态追踪。

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 主键 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses.id` ON DELETE CASCADE | | 关联 parse |
| `imported_by` | `UUID` | NOT NULL | | 导入操作者（QuestionBank 服务账号 ID） |
| `status` | `VARCHAR(20)` | NOT NULL | | `imported` / `failed` |
| `note` | `TEXT` | NULL | | 备注（如失败原因、导入题目数等） |
| `imported_question_ids` | `TEXT` | NULL | | 导入的 QuestionBank 题目 ID 列表（JSON 数组字符串） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间 |

**索引**:
- `PK_document_parse_imports` on `id`（PRIMARY KEY）
- `IX_document_parse_imports_parse_id` on `parse_id`（UNIQUE，一个 parse 至多一条导入记录）

**外键**:
- `parse_id` → `document_parses(id)` ON DELETE CASCADE（parse 删除时联动清除导入记录）

**状态流转**:
- 首次写入：插入 `status=imported` 或 `status=failed`
- 重复写入：UPDATE 已有记录的 `status` / `note` / `imported_question_ids` / `updated_at`，不报错（除"重复 imported"业务校验外）

### 复用现有表（只读）

| 表 | 用途 | 关键字段 |
|----|------|---------|
| `document_parses` | 可导入列表数据源 | `id` / `document_file_id` / `status` / `model_version` / `parsed_at` |
| `document_files` | 列表关联文件名 | `id` / `file_name` |
| `documents` | 列表关联文档元数据（学科/年级/年份） | `id` / `title` / `subject` / `grade` / `year` |
| `document_parse_blocks` | 结构化块检索数据源 | `id` / `parse_id` / `page_id` / `sort_index` / `block_type` / `text_content` / `image_id` / `block_data` |
| `document_parse_images` | 图片访问数据源 | `id` / `parse_id` / `image_name` / `image_path` / `content_type` |

> **注**：`document_parses.document_file_id` 与 `document_files.id` 关联；`document_files` 不直接关联 `documents`（两条链路独立）。学科/年级/年份过滤通过 `documents` 表实现，需借助 `document_parses` ↔ `document_files` ↔ `documents` 的反向查找关系（详见 02-SPEC.md 的关联查询设计）。

## 接口清单

| 方法 | 路径 | 用途 |
|------|------|------|
| GET | `/admin/document-parses/importable` | 列出可导入的 parse（status=parsed） |
| GET | `/admin/document-parses/{parseId}/blocks` | 按 parseId 获取结构化块 |
| GET | `/admin/document-parses/images/{imageId}` | 获取图片二进制流 |
| POST | `/admin/document-parses/{parseId}/import-status` | 回写导入状态 |

## 配置项

无新增配置项。复用现有：
- `Jwt:Issuer` / `Jwt:Audience` / `Jwt:JwksEndpoint` — JWT 验证
- `Oss:Bucket` / `Oss:Endpoint` — 图片存储访问

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| 现有端点（风格参考） | `src/Service/Endpoints/DocumentParseEndpoints.cs` |
| 现有 Parse 服务 | `src/Domain/Services/DocumentParseService.cs`（推断） |
| Parse 仓储 | `src/Database/Repositories/DocumentParseRepository.cs` |
| Block 仓储 | `src/Database/Repositories/DocumentParseBlockRepository.cs` |
| Image 仓储 | `src/Database/Repositories/DocumentParseImageRepository.cs` |
| OSS 服务 | `src/Service/` 下 `IOssService`（来自 ruoyu.common） |
| 建表脚本 | `src/Database/DatabaseInitializer.cs` |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 详细需求规格、验收场景、非功能需求、测试策略 |
