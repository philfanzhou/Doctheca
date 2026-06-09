# DocumentDeletion — 文档删除与级联清理

## 一句话概括

管理员按标题删除不再需要的文档，系统级联删除数据库记录（occurrences → questions → segments → pages → document）、搜索索引、向量索引和 OSS 文件，确保数据一致性，且删除操作幂等。

---

## 核心用户故事

### 故事 1：管理员删除文档及全部关联数据

**角色**：管理员（Admin）

**场景**：
> 作为管理员，我需要删除不再需要的文档，系统应级联删除该文档的所有关联数据（occurrences、questions、segments、pages）、搜索索引、向量索引和 OSS 文件，确保不留孤立数据。

**验收要点**：
- 按标题删除，不存在时返回 true（幂等）。
- 级联删除顺序：occurrences → questions → segments → pages → document → SaveChanges。
- 搜索索引和向量索引在 SaveChanges 之后清理，失败仅记 Error 日志。
- OSS 文件在数据库删除之后清理，失败仅记 Warning 日志。

### 故事 2：运维人员安全清理存储

**角色**：运维人员（Ops）

**场景**：
> 作为运维人员，我需要确保删除文档时 OSS 文件也被清理，释放存储空间。即使 OSS 删除失败，数据库一致性也不受影响。

**验收要点**：
- 数据库记录先删除，OSS 文件后删除。
- OSS 删除失败不阻塞接口返回，仅记 Warning 日志。
- 操作日志完整可审计。

---

## 关键验收条件摘要

1. **幂等性**：按标题删除，文档不存在时返回 true。
2. **级联删除顺序**：occurrences → questions → segments → pages → document → SaveChanges。
3. **搜索索引清理**：在 SaveChanges 之后执行，失败仅记 Error 日志，不影响返回结果。
4. **向量索引清理**：在 SaveChanges 之后执行，失败仅记 Error 日志，不影响返回结果。
5. **OSS 文件清理**：Admin 端点先删数据库再删 OSS，OSS 失败仅记 Warning 日志。
6. **返回格式**：`{ success: true, data: { title, deleted: document != null } }`。

---

## 范围外（Out of Scope）

- **批量删除**：当前仅支持按标题单个删除，不支持批量。
- **软删除**：当前为硬删除，不支持回收站或软删除。
- **删除前引用检查**：不检查文档是否被其他业务引用。
- **删除进行中的文档**：当前不校验文档状态（如 processing），任何状态均可删除。
- **OSS 文件恢复**：OSS 文件删除后不可恢复（建议开启版本控制）。

---

## 文档索引

| 文档 | 路径 | 说明 |
|------|------|------|
| **功能说明**（本文件） | `docs/modules/DocumentDeletion/01-FEATURE.md` | 功能概述、用户故事、验收要点、范围外说明 |
| **规格与功能要求** | [02-SPEC.md](./02-SPEC.md) | 功能清单、详细验收标准、非功能需求、测试策略 |
| **架构与设计** | [03-DESIGN.md](./03-DESIGN.md) | 目录结构、接口签名、数据流、错误处理、外部依赖 |
| **任务与验证** | [04-TASKS.md](./04-TASKS.md) | 代码审查任务列表与每条的自动化验证命令 |
| **测试计划** | [05-TESTS.md](./05-TESTS.md) | 单元测试 / 集成测试 / 边界测试场景与断言 |
| **规范约定** | [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、测试工具、代码风格 |
