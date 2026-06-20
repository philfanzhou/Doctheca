# 迁移历史

## 迁移策略

本项目 **不使用 EF Core Code-First Migration**。表结构通过 `DatabaseInitializer` 在应用启动时以原生 SQL 创建（`CREATE TABLE IF NOT EXISTS`）。

## 当前数据库版本

所有表通过 [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) 的 `GetTableCreationSql` 方法定义。

## 已执行操作

| 时间 | 操作 | 说明 |
|------|------|------|
| 初始 | 创建 6 张表 | documents, document_pages, document_segments, question_segments, document_occurrences, document_ingestion_jobs |
| 初始 | 创建 document_segment_backups 表 | Refine 功能的备份表 |

## 列级迁移（EnsureColumnsAsync）

通过 `DatabaseInitializer.EnsureColumnsAsync` 在启动时自动执行，兼容新旧数据库：

| SQL | 说明 |
|-----|------|
| `ALTER TABLE documents ADD COLUMN IF NOT EXISTS llm_profile_json text NULL` | 新增 LLM 画像字段 |
| `ALTER TABLE document_segments DROP COLUMN IF EXISTS block_id` | 移除 BlockId（SentenceId 格式从 `p{N}-b{M}-s{K}` 简化为 `p{N}-s{K}`） |

## 变更日志

| 日期 | 变更 | 影响 |
|------|------|------|
| 2026-06-20 | 移除 document_segments.block_id 列 | SentenceId 格式简化，BlockId 从代码和数据库中完全移除 |