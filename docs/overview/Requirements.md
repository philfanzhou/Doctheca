# Requirements — 服务级需求

> 本文件是需求摘要入口。详细功能需求见 [modules/](../modules/README.md) 目录。

## 功能需求

### 文档管理与解析

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-01 | 上传文档文件：支持 PDF/DOC/DOCX/PPT/PPTX 上传，文件存入 OSS，记录存入 `document_files` 表 | [MinerUAgentParsing](../modules/MinerUAgentParsing/01-FEATURE.md) |
| FR-02 | MinerU 文档解析：后台 Worker 调用 MinerU Precision API 解析文件，结果持久化到 `document_parses` / `document_parse_blocks` / `document_parse_images` | [MinerUAgentParsing](../modules/MinerUAgentParsing/01-FEATURE.md) |
| FR-03 | 文档元数据分析：MinerU 解析完成后，文档缺 `subject`/`grade`/`year` 时由 LLM best-effort 自动填充 | [DocumentMetadataAnalysis](../modules/DocumentMetadataAnalysis/01-FEATURE.md) |
| FR-04 | 更新文档元数据：通过 `PUT /admin/document-files/{id}/metadata` 修改 `subject`/`grade`/`year`，同步刷新 OpenSearch 索引 | [OpenSearchBlockIndexing](../modules/OpenSearchBlockIndexing/02-SPEC.md) |
| FR-05 | QuestionBank 拉模式导入：向 QuestionBank 服务暴露解析产出（可导入列表、结构化块、图片、导入状态回写） | [QuestionBankImport](../modules/QuestionBankImport/01-FEATURE.md) |

### 文档搜索

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-06 | 精确搜索：基于 OpenSearch 的关键词匹配，支持 subject/grade/year/documentTitle 过滤 | [ExactSearch](../modules/ExactSearch/01-FEATURE.md) |

## 非功能需求

| 编号 | 需求 |
|------|------|
| NFR-01 | 搜索响应时间 < 2s（精确搜索） |
| NFR-02 | 文档解析异步执行（MinerUFileParseWorker），不阻塞上传响应 |
| NFR-03 | OpenSearch 索引失败不阻塞解析主流程，仅记日志 |
| NFR-04 | 支持 PostgreSQL 和 SQLite 双数据库 |
| NFR-05 | 服务单端口：HTTP(5012) |
