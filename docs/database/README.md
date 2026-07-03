# database — 数据库文档

> **本目录是 DocLibrary 服务数据库结构的唯一事实源。** 其他文档如需引用表结构，请链接至 `tables/` 下的对应文件。

## 表清单

| 表名 | 说明 | 文档 |
|------|------|------|
| `documents` | 文档主表（旧管线） | [tables/documents.md](tables/documents.md) |
| `document_pages` | 文档页面表（旧管线） | [tables/document_pages.md](tables/document_pages.md) |
| `document_segments` | 文本片段表（旧管线） | [tables/document_segments.md](tables/document_segments.md) |
| `question_segments` | 题目片段表（旧管线） | [tables/question_segments.md](tables/question_segments.md) |
| `document_occurrences` | 词元出现位置表（旧管线） | [tables/document_occurrences.md](tables/document_occurrences.md) |
| `document_ingestion_jobs` | 文档导入任务表（旧管线） | [tables/document_ingestion_jobs.md](tables/document_ingestion_jobs.md) |
| `document_files` | 文档文件表（新 MinerU 管线） | [tables/document_files.md](tables/document_files.md) |
| `document_parses` | 解析记录表（新 MinerU 管线） | [tables/document_parses.md](tables/document_parses.md) |
| `document_parse_blocks` | 解析 block 表（新 MinerU 管线） | [tables/document_parse_blocks.md](tables/document_parse_blocks.md) |
| `document_parse_images` | 解析图片表（新 MinerU 管线） | [tables/document_parse_images.md](tables/document_parse_images.md) |

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
- **引擎**：PostgreSQL（生产） / SQLite（本地开发，通过连接字符串自动切换）
- **连接字符串**：`ConnectionStrings:Default`