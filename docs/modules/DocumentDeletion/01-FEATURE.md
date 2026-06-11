# 文档删除 (DocumentDeletion)

## 功能名称和一句话概括

文档删除 — 管理员通过 Admin 界面按标题删除文档，系统级联删除关联数据、搜索索引、向量索引和 OSS 文件，实现幂等删除与容错清理。

## 核心用户故事

**管理员删除文档**：作为管理员，我希望通过 Admin 界面删除一份文档，系统自动清理所有关联数据（页面、片段、题目、词元、导入任务、搜索索引、向量索引、OSS 文件），让我无需手动处理级联清理，且在文档不存在时也能安全返回成功。

## 补充约束

- 幂等性：文档不存在时返回成功
- 级联删除顺序：occurrences → questions → segments → pages → document
- 索引清理容错：搜索索引和向量索引清理失败不阻塞删除流程
- OSS 清理容错：OSS 文件删除失败不阻塞删除流程
- 数据库优先：先删数据库记录，再删 OSS 文件

## 关键验收条件摘要

- AC-1：按标题删除文档，文档不存在时返回 `true`（幂等），接口返回 `{ success: true, data: { title, deleted: false } }`。
- AC-2：删除前先取消该文档正在进行（pending/processing）的导入任务，将任务状态置为 `cancelled` 并记录 `FinishedAt`。
- AC-3：级联删除顺序严格为 occurrences → questions → segments → pages → document → SaveChanges，保证数据库一致性。
- AC-4：搜索索引清理在 SaveChanges 之后执行，失败仅记 Error 日志，不影响删除结果。
- AC-5：向量索引（Qdrant）清理在 SaveChanges 之后执行，失败仅记 Error 日志，不影响删除结果。
- AC-6：Admin 端点先获取文档信息（用于 OSS 路径），再调用 `DeleteDocumentAsync` 删除数据库和索引，最后删除 OSS 文件。
- AC-7：OSS 文件删除失败仅记 Warning 日志，不阻塞接口返回成功。
- AC-8：接口返回格式为 `{ success: true, data: { title, deleted: document != null } }`，其中 `deleted` 标识文档是否实际存在过。

## 明确列出"范围外"（不做什么）

- 不处理应用层用户认证与权限校验（访问控制由部署层网络隔离实现，仅内网可访问 `/admin/` 路径）
- 不实现批量删除
- 不实现软删除/回收站
- 不处理文档关联的其他业务数据（如错题引用）

## 文档索引

| 文档 | 说明 |
| --- | --- |
| [02-SPEC.md](./02-SPEC.md) | 详细需求与验收标准清单 |
| [03-DESIGN.md](./03-DESIGN.md) | 架构设计、接口签名与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 开发/验证任务列表 |
| [05-TESTS.md](./05-TESTS.md) | 单元测试与集成测试计划 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
