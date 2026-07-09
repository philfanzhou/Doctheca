# 迁移历史

## 迁移策略

本项目 **不使用 EF Core Code-First Migration**。表结构通过 `DatabaseInitializer` 在应用启动时以原生 SQL 创建（`CREATE TABLE IF NOT EXISTS`）。

## 当前数据库版本

所有表通过 [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) 的 `GetTableCreationSql` 方法定义。

## 列级迁移（EnsureColumnsAsync）

通过 `DatabaseInitializer.EnsureColumnsAsync` 在启动时自动执行，兼容新旧数据库：

| SQL | 说明 |
|-----|------|
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list jsonb NULL` | 新增 content_list（阶段 2） |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS zip_path character varying(500) NULL` | 新增 zip_path（阶段 2） |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_version character varying(20) NOT NULL DEFAULT 'vlm'` | 新增模型版本列 |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list_v2 jsonb NULL` | 新增 MinerU pipeline 输出 v2 |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_json jsonb NULL` | 新增模型推理结果 |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS layout_json jsonb NULL` | 新增版面分析数据 |
| `ALTER TABLE document_parses DROP COLUMN IF EXISTS layout_pdf_path` | 删除旧列（被 layout_json 替代） |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject character varying(50) NULL` | 新增学科元数据列（DocumentMetadataAnalysis 功能） |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade character varying(20) NULL` | 新增年级元数据列（DocumentMetadataAnalysis 功能） |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year character varying(10) NULL` | 新增年份元数据列（DocumentMetadataAnalysis 功能） |

## 变更日志

| 日期 | 变更 | 影响 |
|------|------|------|
| 2026-07-04 | 新增 document_files.subject/grade/year 列 | 文档元数据分析功能，支持手动设置和 LLM 自动填充 |
| 2026-07-04 | 新增 document_parses.content_list_v2 / model_json / layout_json 列 | MinerU pipeline/vlm 模式输出结构化数据 |
| 2026-07-04 | 新增 document_parse_blocks 表和 document_parse_images 表 | OpenSearch 块级索引和图片管理 |
| 2026-07-04 | 删除 document_parses.layout_pdf_path 列 | 被 layout_json 替代（JSONB 存储完整版面数据） |
| 2026-07-04 | 新增 document_parses.model_version 列（默认 'vlm'） | 支持 vlm / pipeline 双模型版本 |