# 文档元数据更新（DocumentMetadata）

## 功能名称与一句话概括

**功能名称**：文档元数据更新

**一句话概括**：管理员修改已就绪文档的学科、年级、年份、标签等元数据，修改后搜索索引中的元数据同步更新。

---

## 核心用户故事

**User Story — 管理员视角**

> 作为一名管理员，我希望修改已就绪文档的学科、年级、年份、标签等元数据，并且修改后搜索索引中的元数据也能同步更新，从而保证文档信息在系统和搜索结果中保持一致。

---

## 关键验收条件摘要（可测试）

1. **AC-1：仅 ready 状态可修改** — 只有 status 为 "ready" 的文档允许修改元数据。
2. **AC-2：文档不存在抛异常** — 按标题查找，不存在时抛 `DocRetrievalValidationException("文档不存在")`。
3. **AC-3：未就绪抛异常** — 非 ready 状态抛 `DocRetrievalValidationException("文档未就绪，不允许修改元数据")`。
4. **AC-4：学科与年级校验** — 学科仅支持"英语"，年级必须 K/G1~G12。
5. **AC-5：null 参数不修改** — null 参数表示不修改，仅更新非 null 字段。
6. **AC-6：更新时间戳** — 更新后设置 `UpdatedAt = DateTimeOffset.UtcNow`。
7. **AC-7：搜索索引同步** — 同步调用 `ISearchIndexService.UpdateDocumentMetadataAsync`，失败仅记 Error 日志；同步调用 `IQdrantService.UpdateDocumentMetadataAsync`，失败仅记 Error 日志。
8. **AC-8：Admin 端点至少一项** — 至少提供一项元数据，否则返回 400。
9. **AC-9：tags 存储格式** — tags 字段存储为 JSON 数组字符串，使用 `GetRawText()` 获取。
10. **AC-10：错误码映射** — 不存在→404 NOT_FOUND，未就绪→422 NOT_READY，学科无效→400，年级无效→400。

---

## 范围外（不做什么）

- **不修改文档内容**：本功能只修改元数据，不涉及文档文件或解析内容的变更。
- **不修改文档状态**：元数据更新不改变文档的 status 字段。
- **不实现批量更新**：每次只更新一个文档的元数据。
- **不修改标题**：标题作为路由参数用于定位文档，不在更新范围内。
- **不处理搜索索引重建**：仅同步更新元数据，不触发全文索引重建。

---

## 文档索引

| 文档 | 链接 |
|------|------|
| 功能规格与验收标准 | [SPEC.md](./02-SPEC.md) |
| 技术设计与数据流 | [DESIGN.md](./03-DESIGN.md) |
| 任务与代码审查清单 | [TASKS.md](./04-TASKS.md) |
| 测试场景与代码骨架 | [TESTS.md](./05-TESTS.md) |
| 命名与代码规范 | [CONVENTIONS.md](./06-CONVENTIONS.md) |
