# database — 数据库文档

> **本目录是 DocLibrary 服务数据库结构的唯一事实源。** 其他文档如需引用表结构，请链接至 `tables/` 下的对应文件。

## 表清单

| 表名 | 说明 | 文档 |
|------|------|------|
| `documents` | 文档主表 | [tables/documents.md](tables/documents.md) |
| `document_pages` | 文档页面表 | [tables/document_pages.md](tables/document_pages.md) |
| `document_segments` | 文本片段表 | [tables/document_segments.md](tables/document_segments.md) |
| `question_segments` | 题目片段表 | [tables/question_segments.md](tables/question_segments.md) |
| `document_occurrences` | 词元出现位置表 | [tables/document_occurrences.md](tables/document_occurrences.md) |
| `document_ingestion_jobs` | 文档导入任务表 | [tables/document_ingestion_jobs.md](tables/document_ingestion_jobs.md) |

## 实体关系

详见 [relations.md](relations.md)。

## 迁移历史

本项目不使用 EF Core Migration，而是通过 [DatabaseInitializer](../../src/Database/DatabaseInitializer.cs) 使用 SQL-based 初始化策略。表结构在应用启动时自动创建（`CREATE TABLE IF NOT EXISTS`）。

## 已移除的列

| 表 | 列 | 移除方式 | 说明 |
|----|-----|---------|------|
| `document_segments` | `block_id` | `EnsureColumnsAsync` 启动时自动执行 `DROP COLUMN IF EXISTS` | SentenceId 格式从 `p{N}-b{M}-s{K}` 简化为 `p{N}-s{K}`，BlockId 已无意义 |

## 数据库配置

- **数据库名**：`ruoyu_study_doclibrary`
- **旧数据库名**：`ruoyu_study_docretrieval`（已通过 `DatabaseNameMigrationService` 自动迁移，详见部署文档）
- **引擎**：PostgreSQL（生产） / SQLite（本地开发，通过连接字符串自动切换）
- **连接字符串**：`ConnectionStrings:Default`