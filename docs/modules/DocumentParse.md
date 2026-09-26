# DocumentParse — 文档解析（StructaDoc 管线）

> 按 [ADR-0009](../../../../../docs/adr/0009-doctheca-structadoc-parse-migration.md)，解析管线已从自维护 MinerU 实现迁移到外部 StructaDoc 服务。本文档是原 DocumentParse 六件套收敛后的单一能力文档。

## 能力概述

- 异步解析生命周期：触发 → `pending` → Worker 提交 StructaDoc Parse Run → `parsing` → 终态同步 → `parsed` / `failed`。
- 原件与解析产物（Markdown、图片、ZIP、规范化 PDF）由 StructaDoc 主责存储；本服务只保存 documentId/parseRunId 引用，并把 Blocks/Markdown/Assets 同步到本地表，供搜索、详情、导出消费。
- 存量兼容：迁移前的解析记录只读保留（其 `content_list*`/`model_json`/`layout_json`/`zip_path` 列不再写入新数据）；迁移前上传的文件在首次触发解析时由 Worker 惰性上传到 StructaDoc 并回填引用。

## 端点与契约

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/admin/document-files/{id}/parse?modelVersion=vlm\|pipeline` | 触发解析（校验与错误码见 [DocumentManagement](./DocumentManagement.md)） |
| GET | `/admin/document-parses/{parseId}/images/{imageId}/content` | 新解析图片代理：从 StructaDoc 流式读取 Asset 字节；浏览器经 admin Cookie 认证，可直接用于 `<img>` |

解析状态枚举：`pending` / `parsing` / `parsed` / `failed`（`document_parses.status`）。
未配置 StructaDoc 时触发解析返回 503 + `DOCTHECA_STRUCTADOC_NOT_CONFIGURED`。

## 后台 Worker（StructaDocParseWorker）

每 5 秒轮询 `pending` + `parsing` 记录：

1. **pending**：
   - 文件无 `structadoc_document_id` 且有 `file_path`（存量）→ 从 OSS 下载并上传 StructaDoc，回填引用（惰性迁移）；两者皆无 → failed。
   - `POST parse-runs`（`Idempotency-Key` = parseId 的 "N" 格式；`modelVersion` 命中 `StructaDoc:ProviderConfigIdByModel` 时携带 `providerConfigId`，否则用 StructaDoc 默认 Provider）。
   - 成功 → `parsing`，`external_task_id` 与 `structadoc_parse_run_id` 记录 run id。
2. **parsing**：轮询 `GET /api/v1/parse-runs/{id}`：
   - `succeeded` → 执行结果同步，随后 best-effort OpenSearch 索引与 LLM 元数据分析；
   - `failed` / `cancelled` → 本地 `failed`，`error_message` 携带 StructaDoc `errorCode: errorMessage`；
   - run 404 → `failed`（run 不再存在）；
   - 记录无 `structadoc_parse_run_id`（迁移中断的遗留 `parsing`）→ `failed`，提示重新触发。
3. **失败语义**：transient 错误（网络、超时、408/429/5xx）保持当前状态，下一轮重试；permanent 错误写入 `failed`。

## 结果同步（StructaDocParseResultSync）

- **Assets → `document_parse_images`**：`image_name` = asset.name；`image_path` = asset id（Guid 字符串，**不是 OSS 路径**）；`content_type` = asset.mediaType（缺省 `image/jpeg`）。
- **Blocks → `document_parse_blocks`**（按 `sequence` 排序）：
  - `page_id` = pageNumber − 1（1-based → 本地 0-based；null → 0）；`sort_index` 页内递增；
  - `block_type` = type（截断 20）；`text_content` = content；`sub_type` = subtype；
  - `text_level`：subtype `heading-N` → N，否则 −1；`text_format` = contentFormat；
  - bbox：StructaDoc 0–1 归一化坐标 ×1000，对齐本地 0–1000 约定；`score` = confidence；
  - `image_id`：block.assetId → 本地 image 记录映射；`block_data` = block 的规范化 JSON。
- **Markdown → `document_parses.markdown_content`**；状态置 `parsed` 并记录 `parsed_at`。
- **幂等**：同步前删除本 parse 旧 images（blocks 由仓储先删后插），崩溃后重跑安全。

## StructaDoc 客户端契约摘要（IStructaDocClient）

- 认证：`Authorization: ApiKey <credential>`，需 `documents:write`、`parses:read`、`parses:write` scope。
- `POST /api/v1/documents`（multipart 字段 `file`，201）；`POST /api/v1/documents/{id}/parse-runs`（201 首次 / 200 + `Idempotency-Replayed` 重放）。
- `GET /api/v1/parse-runs/{id}`：终态仅 `succeeded` / `failed` / `cancelled`。
- `GET .../blocks?limit=1000&afterSequence=`：必须跟随 `nextSequence` 翻页到 null。
- `GET .../assets`、`.../markdown`、`.../assets/{assetId}/content`（直接字节流，无签名 URL）。
- `DELETE /api/v1/documents/{id}`（202 受理 / 404 幂等；存在未终态 run 时先 cancel）；`DELETE /api/v1/parse-runs/{id}`（仅终态）。
- 错误：RFC 7807 problem+json（401/403 为空 body，不得解析）；408/429/5xx/网络错误分类为 transient。

## 数据与配置

- 新列：`document_parses.structadoc_parse_run_id`、`document_files.structadoc_document_id`；`document_files.file_path` 改为可空（仅存量使用）。详见 [database/](../database/README.md)。
- 配置节 `StructaDoc`：`BaseUrl`（Consul KV `service-endpoints.json`）、`ApiKey`（start.sh 环境变量 `StructaDoc__ApiKey` 注入，不落仓库）、`TimeoutSeconds`（默认 300）、`ProviderConfigIdByModel`（可选，`vlm`/`pipeline` → StructaDoc Provider Config ID）。
- Provider 侧要求：MinerU Cloud Provider 配置 `model_version=vlm`、`is_ocr`、`enable_formula`、`enable_table` 对齐迁移前解析质量。

## 验证

- 单元测试：`src/Tests/Doctheca.Tests/`（`StructaDoc/StructaDocClientTests`、`Parsing/StructaDocParseResultSyncTests`、`Parsing/ParseImageContentSourceTests`、`StructaDocParseWorkerTests`）。
- 命令：`dotnet test src/Doctheca.sln --configuration Release`（在本服务目录）。
- 切换流量前须在真实环境以真实文档验证 `vlm` + 批量端点组合（ADR-0009 后果条款）。
