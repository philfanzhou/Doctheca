# Agent Review Notes

## 1. 本轮任务范围

对 `ruoyu.docretrieval` 服务的 `docs/` 目录进行全面整理和优化，遵循新规范（薄顶层、细粒度下沉、唯一事实源、development/ 目录支持）。

## 2. 已完成的文档调整

### 2.1 本轮新增文件

| 文件 | 说明 |
|------|------|
| `docs/development/README.md` | 开发执行支持入口 |
| `docs/development/local-setup.md` | 本地环境搭建与运行 |
| `docs/development/verification.md` | 服务验证方法 |

### 2.2 本轮修正项

| # | 文件 | 修正内容 |
|---|------|---------|
| 1 | `docs/modules/README.md` | 14 个断链修复（双层目录 → 单层） |
| 2 | `docs/database/README.md` | 去重 ER 图，改为链接到 `relations.md` |
| 3 | `docs/Integration.md` | 补充上传文件格式（PDF/DOC/DOCX/PPT/PPTX）和大小限制（200MB） |
| 4 | `docs/README.md` | 补充 development/ 目录导航 |
| 5 | `ExactSearch/04-TASKS.md` | 测试路径 `test/Services/` → `test/Ruoyu.Study.DocRetrieval.Tests/` |
| 6 | `HybridSearch/04-TASKS.md` | 同上 |
| 7 | `DocumentParsing/04-TASKS.md` | 测试路径修正 |
| 8 | `DocumentList/05-TESTS.md` | 测试路径和命名空间修正 |
| 9 | `DocumentMetadata/05-TESTS.md` | 测试路径和命名空间修正 |
| 10 | `DocumentParsing/05-TESTS.md` | 命名空间 `Tests.Service` → `Tests` |
| 11 | `HybridSearch/05-TESTS.md` | 测试路径修正 |
| 12 | `DocumentMetadata/03-DESIGN.md` | 依赖表补充 `IQdrantService.UpdateDocumentMetadataAsync` |
| 13 | `DocumentMetadata/06-CONVENTIONS.md` | 补充 `IQdrantService?` 可选依赖、代码风格和审查清单 |

### 2.3 前轮已完成的修正（保留记录）

| # | 修正内容 |
|---|---------|
| 1 | Integration.md HTTP 路径 `/api/` → `/admin/`，参数 `{id}` → `{title}` |
| 2 | Requirements.md 全部 9 个模块链接修正 |
| 3 | database/tables 状态值 `parsing` → `processing`，`completed` → `success` |
| 4 | database/README.md SQLite 切换机制修正 |
| 5 | DocumentUpload 系统性拼写错误 `DOCRETREIVAL_` → `DOCRETRIEVAL_`（62 处） |
| 6 | DocumentParsing 轮询间隔 10s → 5s |
| 7 | DocumentMetadata 01/02/03 补充 Qdrant 同步 |
| 8 | DocumentList/Metadata 01-FEATURE.md 断链修复 |
| 9 | DocumentDeletion 03-DESIGN.md `DeleteAsync` 返回类型修正 |
| 10 | ExactSearch/HybridSearch 03-DESIGN.md `ISearchDomainService` 接口修正 |
| 11 | 不存在的测试文件标记 `[当前无测试覆盖]` |

## 3. 待人工审核事项

### 3.1 日志和注释语言不一致

- **编号**：REV-01
- **问题描述**：DotNetCodingPolicy 4.4 节要求"注释和字符串必须使用英文"，但 DocRetrieval 服务代码中大量使用中文日志和中文异常消息
- **已查阅的证据**：`DocumentDomainService.cs`、`DocumentAdminEndpoints.cs`、`IngestionWorker.cs` 中的 ILogger 调用
- **为什么仍无法完全确认**：不确定这是有意为之（面向中文开发团队）还是遗漏。其他服务（如 ruoyu.mistake）也使用中文日志
- **建议复核方式**：确认团队是否统一接受中文日志，如是则更新 DotNetCodingPolicy 的例外说明

### 3.2 DocumentDeletion 权限控制缺失

- **编号**：REV-02
- **问题描述**：`DocumentDeletion/02-SPEC.md` 中 REQ-DEL-08 写"仅管理员可调用此接口"，但代码中 `DeleteDocument` 端点无任何授权检查
- **已查阅的证据**：`DocumentAdminEndpoints.cs` 的 `DeleteDocument` 方法无 `[Authorize]` 特性或权限校验逻辑
- **为什么仍无法完全确认**：可能由上层 API 网关统一处理鉴权
- **建议复核方式**：确认 API 网关是否对所有 `/admin/` 路径做了鉴权；如是，在 SPEC 中注明"鉴权由网关负责"

### 3.3 HybridSearch 中 `stemmed` MatchType

- **编号**：REV-03
- **问题描述**：`HybridSearch/06-CONVENTIONS.md` 列出 `stemmed` 作为 MatchType 值，但代码中 `OpenSearchIndexService.ExactSearchAsync` 只产生 `exact_phrase` 和 `exact_word`，`stemmed` 仅存在于优先级映射字典中，从未被实际生成
- **已查阅的证据**：`OpenSearchIndexService.cs` 的 `matchTypePriority` 字典和 `ExactSearchAsync` 方法
- **为什么仍无法完全确认**：`stemmed` 可能是预留的、或由 OpenSearch 内部匹配类型映射产生但在当前代码路径中未触发
- **建议复核方式**：确认 `stemmed` 是否为未来功能预留，如是则标注 `[待实现]`

## 4. 证据不足但已落盘的内容

| 内容 | 文件 | 标注 |
|------|------|------|
| DotNetCodingPolicy 中"注释必须英文"与实际代码中文日志的矛盾 | `docs/DotNetCodingPolicy.md` | `[推断]` |
| `source_type` 字段的 `word`/`ppt` 值来自代码常量推断 | `docs/database/tables/documents.md` | `[推断]` |
| `cancelled` 状态已确认存在于代码（`DocumentDomainService.cs:292`） | `docs/database/tables/document_ingestion_jobs.md` | 已验证 ✓ |

## 5. 风险与后续建议

1. **测试覆盖缺口**：IngestionWorker、DocumentDeletion、OpenSearchIndexService 均无单元测试，建议优先补充
2. **文档与代码同步机制**：当前无自动化校验，建议在 CI 中加入文档链接检查步骤
3. **状态枚举硬编码**：文档状态和任务状态在代码中以字符串硬编码，建议统一为枚举常量
4. **DocumentMetadata 测试骨架**：05-TESTS.md 中的测试骨架类缺少 `Mock<IQdrantService>` 注入，实际运行时 Qdrant 同步路径不会被覆盖