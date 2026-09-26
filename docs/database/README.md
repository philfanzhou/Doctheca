# database — 数据库文档

> **本目录是 Doctheca 服务数据库结构的唯一事实源。** 其他文档如需引用表结构，请链接至 `tables/` 下的对应文件。

## 表清单

| 表名 | 说明 | 文档 |
|------|------|------|
| `document_files` | 文档文件表（解析管线） | [tables/document_files.md](tables/document_files.md) |
| `document_parses` | 解析记录表（解析管线） | [tables/document_parses.md](tables/document_parses.md) |
| `document_parse_blocks` | 解析 block 表（解析管线） | [tables/document_parse_blocks.md](tables/document_parse_blocks.md) |
| `document_parse_images` | 解析图片表（解析管线） | [tables/document_parse_images.md](tables/document_parse_images.md) |

遗留 `document_parse_imports` 表已退出运行时模型；升级不自动删除已有表，兼容说明见 [tables/document_parse_imports.md](tables/document_parse_imports.md)。

## 实体关系

详见 [relations.md](relations.md)。

## 迁移历史

本项目不使用 EF Core Migration，而是通过 [DatabaseInitializer](../../src/Database/DatabaseInitializer.cs) 使用 SQL-based 初始化策略。表结构在应用启动时自动创建（`CREATE TABLE IF NOT EXISTS`）。

## 数据库配置

- **数据库名**：`doctheca`
- **引擎**：PostgreSQL
- **连接字符串**：`ConnectionStrings:Default`
