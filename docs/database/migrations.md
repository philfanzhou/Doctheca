# 迁移历史

## 迁移策略

本项目 **不使用 EF Core Code-First Migration**。表结构通过 `DatabaseInitializer` 在应用启动时以原生 SQL 创建（`CREATE TABLE IF NOT EXISTS`）。

## 当前数据库版本

所有表通过 [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) 的 `GetTableCreationSql` 方法定义。

## 列级迁移（EnsureColumnsAsync）

通过 `DatabaseInitializer.EnsureColumnsAsync` 在启动时自动执行，兼容新旧数据库：

| SQL | 说明 |
|-----|------|
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject varchar(50) NULL` | 新增学科元数据列（DocumentMetadataAnalysis 功能） |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade varchar(20) NULL` | 新增年级元数据列（DocumentMetadataAnalysis 功能） |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year varchar(10) NULL` | 新增年份元数据列（DocumentMetadataAnalysis 功能） |

## 变更日志

| 日期 | 变更 | 影响 |
|------|------|------|
| 2026-07-04 | 新增 document_files.subject/grade/year 列 | 文档元数据分析功能，支持手动设置和 LLM 自动填充 |