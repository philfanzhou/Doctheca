# document_parse_imports — 遗留兼容表

> 下文的 Quaestura 是原 QuestionBank 服务，已迁出为独立仓库（ADR-0011）。此表在迁出前即已从 DocLibrary 移除。

`document_parse_imports` 曾用于由 Quaestura 向 DocLibrary 回写导入状态。该设计无法与 Quaestura 题目写入处于同一数据库事务，不能可靠防止重复导入，并造成跨服务数据所有权混乱。

当前 DocLibrary 不提供 Quaestura 集成接口；如果未来重新建立集成，导入幂等责任应由 Quaestura 承担：

- DocLibrary 运行时不再映射、查询、创建或更新此表。
- `DatabaseInitializer` 不再创建此表。
- 升级过程不会自动执行 `DROP TABLE`，已有环境中的表和数据保持不动。
- 如需删除遗留表，必须通过单独的备份、影响检查和显式 SQL 变更完成。

历史字段仅用于识别遗留数据：`id`、`parse_id`、`imported_by`、`status`、`note`、`imported_question_ids`、`created_at`、`updated_at`。
