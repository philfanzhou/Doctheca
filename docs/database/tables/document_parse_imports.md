# document_parse_imports — 导入状态表

> 本文件是 `document_parse_imports` 表的唯一事实源。

## 设计背景

`document_parse_imports` 表记录 QuestionBank 服务对某个 parse 的导入状态，用于防重复处理与状态追踪。由 [QuestionBankImport 模块](../../modules/QuestionBankImport/01-FEATURE.md) 引入。

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | 主键 |
| `parse_id` | `UUID` | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE, UNIQUE | | 关联 parse（一个 parse 至多一条导入记录） |
| `imported_by` | `UUID` | NOT NULL | | 导入操作者（QuestionBank 服务账号 ID） |
| `status` | `VARCHAR(20)` | NOT NULL | | `imported` / `failed` |
| `note` | `TEXT` | NULL | | 备注（如失败原因、导入题目数等） |
| `imported_question_ids` | `TEXT` | NULL | | 导入的 QuestionBank 题目 ID 列表（JSON 数组字符串） |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | 创建时间 |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NULL | | 最后更新时间 |

## 索引

| 索引名 | 列 | 说明 |
|--------|-----|------|
| `PK_document_parse_imports` | `id` | 主键 |
| `IX_document_parse_imports_parse_id` | `parse_id` | UNIQUE，一个 parse 至多一条导入记录 |

## 外键

- `parse_id` → `document_parses(id)` ON DELETE CASCADE（parse 删除时联动清除导入记录）

## 状态流转

```
首次写入 → 插入 status=imported 或 status=failed
重复写入 → UPDATE 已有记录的 status / note / imported_question_ids / updated_at
  ├─ imported → imported：拒绝（422 DOCLIBRARY_PARSE_ALREADY_IMPORTED）
  ├─ imported → failed：允许
  └─ failed → imported / failed：允许
```

## 特殊说明

- **UNIQUE 约束保证并发安全**：`parse_id` 上有 UNIQUE 约束，并发写入时捕获 UniqueViolation 后转为 UPDATE。
- **ON DELETE CASCADE**：删除 `document_parse` 记录时，关联的导入记录自动清除。
- **只写接口**：本表仅通过 `POST /admin/document-parses/{parseId}/import-status` 写入，由 QuestionBank 服务调用。
