# 迁移历史

## 迁移策略

本项目 **不使用 EF Core Code-First Migration**。表结构通过 `DatabaseInitializer` 在应用启动时以原生 SQL 创建（`CREATE TABLE IF NOT EXISTS`）。

## 当前数据库版本

所有表通过 [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs) 的 `GetTableCreationSql` 方法定义。

## 已执行操作

| 时间 | 操作 | 说明 |
|------|------|------|
| 初始 | 创建 6 张表 | documents, document_pages, document_segments, question_segments, document_occurrences, document_ingestion_jobs |

## 变更日志

（尚无 schema 变更记录）