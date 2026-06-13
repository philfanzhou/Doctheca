# Agent Annotation Resolution Audit

## 1. 本轮任务范围

基于 `docs/AgentReviewNotes.md` 中的人工批注（REV-01/02/03），对 `ruoyu.docretrieval` 服务的代码、文档执行精准整改，完成闭环验证。

## 2. 启动校验结果

| 校验项           | 结果                                                              |
| ------------- | --------------------------------------------------------------- |
| 目标项目路径存在      | ✅ `/Users/phil/Code/git/Ruoyu.Study/backend/ruoyu.docretrieval` |
| 批注文档存在且可读取    | ✅ `docs/AgentReviewNotes.md`，含 REV-01/02/03/04                  |
| 正式 docs 根目录存在 | ✅ `docs/` 含 overview/modules/database/Integration/development   |
| 批注中代码路径可定位    | ✅ 所有引用文件均已核实                                                    |
| 工作区未提交改动      | 无无关改动（本轮为首次修改）                                                  |
| 禁改区域          | 无明确禁改区域                                                         |
| 批注总表已建立       | ✅ 见下节                                                           |

**判定：可继续执行**

## 3. 批注总表

| 批注编号   | 来源位置                  | 关联功能点/模块                       | 人工批注意图               | 当前状态    |
| ------ | --------------------- | ------------------------------ | -------------------- | ------- |
| REV-01 | AgentReviewNotes §3.1 | 全服务：日志/注释语言                    | 修改代码和日志为英文（非修改规范）    | **已完成** |
| REV-02 | AgentReviewNotes §3.2 | DocumentDeletion 权限控制          | 确认为设计意图，更新文档说明部署层控制  | **已完成** |
| REV-03 | AgentReviewNotes §3.3 | HybridSearch stemmed MatchType | stemmed 是要求实现的功能，非预留 | **已完成** |
| REV-04 | AgentReviewNotes §3.4 | DocumentList 02-SPEC 缺失        | 已补齐，无需进一步处理          | **已完成** |

## 4. 每条批注的处理记录

### REV-01 — 日志和注释语言统一为英文

- **批注原文摘要**：DotNetCodingPolicy 4.4 要求"注释和字符串必须使用英文"，但代码中大量使用中文日志和中文异常消息。人工批注："不是，需要修改代码或者日志。"
- **解读后的整改目标**：将所有 C# 源代码中的中文日志消息、异常消息、注释、HTTP 响应消息改为英文。
- **已核查的代码/测试/配置/文档证据**：
  - 涉及 **9 个源文件** + **2 个接口文件** + **1 个模型文件** + **1 个常量文件** + **1 个 DbContext 文件**
  - 共约 **100+ 处** 中文字符串
- **根因判断**：开发时未遵循 DotNetCodingPolicy 4.4 节
- **实际修改项**：

| 文件                                                  | 修改类型         | 修改数量   |
| --------------------------------------------------- | ------------ | ------ |
| `src/Domain/Services/DocumentDomainService.cs`      | 日志+异常+注释     | 31 处   |
| `src/Domain/Services/SearchDomainService.cs`        | 日志+注释        | 4 处    |
| `src/Service/DocumentRetrievalServiceImpl.cs`       | 异常消息         | 3 处    |
| `src/Service/DocumentAdminEndpoints.cs`             | 响应消息+注释+匹配逻辑 | 24 处   |
| `src/Service/OpenSearchIndexService.cs`             | 日志+注释+异常     | 22 处   |
| `src/Service/IngestionWorker.cs`                    | 日志+注释+异常     | 25 处   |
| `src/Service/QdrantService.cs`                      | 日志+注释        | 31 处   |
| `src/Service/DocumentParserService.cs`              | 注释+异常+日志     | \~70 处 |
| `src/Host/Program.cs`                               | 日志           | 4 处    |
| `src/Domain/Repositories/ISearchIndexService.cs`    | XML 文档注释     | 6 处    |
| `src/Domain/Repositories/IDocumentParserService.cs` | XML 文档注释     | 5 处    |
| `src/Domain/Models/DocumentModels.cs`               | XML 文档注释     | 6 处    |
| `src/Domain/Models/Constants.cs`                    | 注释+值标签       | 16 处   |
| `src/Database/DocRetrievalDbContext.cs`             | 行内注释         | 1 处    |
| `test/.../ConstantsTests.cs`                        | 断言值          | 3 处    |
| `test/.../DocumentDomainServiceTests.cs`            | 断言字符串        | 11 处   |
| `test/.../SearchDomainServiceTests.cs`              | 断言字符串        | 1 处    |

- **同步更新的正式文档**：
  - `docs/overview/DotNetCodingPolicy.md` — 补充表更新：日志语言和注释语言从"中文"改为"英文"，新增"域值例外"行说明数据库域值常量保留中文的原因
- **实际执行的验证命令**：
  - `dotnet build` → **0 Warning, 0 Error, Build succeeded**
  - `dotnet test` → **104 passed, 0 failed**
- **结果摘要**：全部源代码中的中文已清除（仅保留 `SubjectEnglish = "英语"` 和 `GradeLabels` 中文值作为域数据常量）。构建通过，全量测试通过。

### REV-02 — DocumentDeletion 权限控制文档修正

- **批注原文摘要**：REQ-DEL-08 写"仅管理员可调用此接口"，但代码无授权检查。人工批注："是，这是设计意图。部署阶段会控制只有内网才能访问管理界面"
- **解读后的整改目标**：更新文档，明确权限控制由部署层网络隔离实现
- **已核查的证据**：
  - `DocumentAdminEndpoints.cs` DeleteDocument 无 `[Authorize]`
  - `Program.cs` 无鉴权中间件注册
- **根因判断**：文档描述了应用层鉴权，但设计意图是部署层控制
- **实际修改项**：
  - `docs/modules/DocumentDeletion/02-SPEC.md` REQ-DEL-08 → 补充部署层网络隔离说明
  - `docs/modules/DocumentDeletion/02-SPEC.md` §4.2 安全 → 重写为部署层控制描述
  - `docs/modules/DocumentDeletion/01-FEATURE.md` "范围外"章节 → 补充访问控制由部署层实现
- **同步更新的正式文档**：同上（均为文档内部修正）
- **实际执行的验证命令**：文档审查
- **结果摘要**：文档与设计意图一致，消除了误导性的"应用层鉴权"描述

### REV-03 — 实现 stemmed MatchType 功能

- **批注原文摘要**：`stemmed` 仅存在于优先级映射字典中，从未被代码产生。人工批注："不是未来预留，这是要求实现的功能"
- **解读后的整改目标**：
  1. OpenSearch 非短语搜索经过词干提取分析器，MatchType 应标记为 `"stemmed"` 而非 `"exact_word"`
  2. 数据库回退路径中的 `"stem_match"` 应统一为 `"stemmed"`
- **已核查的证据**：
  - `OpenSearchIndexService.cs:392`：非短语搜索用 `english_custom` 分析器（含 stemming），但返回 `"exact_word"`
  - `SearchDomainService.cs:77`：数据库回退将 `exact_word` 改为 `stem_match`（与字典中的 `stemmed` 不一致）
  - `OpenSearchIndexService.cs:467-473`：优先级字典含 `stemmed(2)` 但不含 `stem_match`
- **根因判断**：OpenSearch 非短语搜索的 MatchType 标注不准确；数据库回退使用了不一致的名称
- **实际修改项**：

| 文件                                               | 修改内容                                                                 |
| ------------------------------------------------ | -------------------------------------------------------------------- |
| `src/Service/OpenSearchIndexService.cs:L392`     | `MatchType = phrase ? "exact_phrase" : "stemmed"` （原 `"exact_word"`） |
| `src/Domain/Services/SearchDomainService.cs:L77` | `r.MatchType = "stemmed"` （原 `"stem_match"`）                         |
| `test/.../SearchDomainServiceTests.cs:L203-208`  | 断言从 `"stem_match"` 更新为 `"stemmed"`                                   |

- **同步更新的正式文档**：
  - `docs/modules/ExactSearch/02-SPEC.md` FR-10 / AC-FR-10 → 区分 OpenSearch 路径 (`stemmed`) 和数据库回退路径 (`exact_word`)
  - `docs/modules/HybridSearch/02-SPEC.md` FR-07 / AC-FR-07 → `stem_match` → `stemmed`
- **实际执行的验证命令**：
  - `dotnet build` → **0 Warning, 0 Error, Build succeeded**
  - `dotnet test` → **104 passed, 0 failed**
- **结果摘要**：`stemmed` MatchType 现在在 OpenSearch 非短语搜索路径中被正确产生；`stem_match` 已统一为 `stemmed`；优先级字典中的 `stemmed(2)` 不再是死代码

## 5. 正式文档同步记录

| 文档路径                                          | 同步原因                       | 更新摘要                                                             |
| --------------------------------------------- | -------------------------- | ---------------------------------------------------------------- |
| `docs/overview/DotNetCodingPolicy.md`         | REV-01：代码已改为英文             | 补充表更新：日志/注释语言→英文，新增域值例外说明                                        |
| `docs/modules/DocumentDeletion/02-SPEC.md`    | REV-02：权限控制说明              | REQ-DEL-08 和 §4.2 安全章节重写为部署层网络隔离                                 |
| `docs/modules/DocumentDeletion/01-FEATURE.md` | REV-02：权限控制说明              | "范围外"章节补充部署层控制说明                                                 |
| `docs/modules/HybridSearch/02-SPEC.md`        | REV-03：stem\_match→stemmed | FR-07、AC-FR-07 统一为 stemmed                                       |
| `docs/modules/ExactSearch/02-SPEC.md`         | REV-03：MatchType 说明更新      | FR-10、AC-FR-10 区分 OpenSearch(stemmed) 与 DB fallback(exact\_word) |
| `docs/AgentReviewNotes.md`                    | 全局同步                       | REV-01/02/03 标记为已整改并填写整改结果                                       |

## 6. 未完成或暂时跳过的批注

无。所有 4 条批注均已闭环处理。

## 7. 风险与待人工复核事项

1. **域值常量的英文化边界**：`SubjectEnglish = "英语"` 和 `GradeLabels` 的中文值保留未改。这些值存储在数据库中并被业务逻辑引用。如果未来需要国际化显示，应在展示层做翻译而非修改常量。 **→ 已处理**：`SubjectEnglish` 已改为 `"English"`，`GradeLabels` 中文值已改为英文（Kindergarten, Grade 1 等），见 commit `cfe5c34`。
2. **测试覆盖缺口**（来自原 AgentReviewNotes §5，本轮未涉及）：IngestionWorker、DocumentDeletion、OpenSearchIndexService 均无单元测试。 **→ 已处理**：IngestionWorker 6 个 UT（commit `37e3d9d`），OpenSearchIndexService 7 个 JSON 解析 UT（commit `13a6b6c`），DocumentDeletion 测试已补齐。
3. **状态枚举硬编码**（来自原 AgentReviewNotes §5）：建议统一为枚举常量，降低拼写错误风险。 **→ 已处理**：已创建 `DocumentStatus`、`SegmentTypes`、`SourceTypes`、`SearchMatchType` 四个静态常量类，全项目魔法字符串已替换，见 commit `cfe5c34` 和 `a27f775`。

***

## 8. 语义搜索功能移除记录（2026-06-12）

**变更类型**：功能移除
**变更范围**：HybridSearch（语义搜索）、Qdrant 向量数据库、SiliconFlow Embedding API
**变更原因**：项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API
**影响文档**：共 20+ 个文档文件，均以删除线/标注方式标记"已移除"，保留文档结构供未来参考

受影响的文档清单：

- `docs/modules/HybridSearch/` 全部 6 个文件：添加移除标注
- `docs/modules/README.md`：HybridSearch 条目标注已移除
- `docs/overview/KeyFlows.md`：混合搜索流程标注已移除，上传流程移除 Qdrant 列
- `docs/overview/Requirements.md`：FR-09 标注已移除，NFR-01/03 标注删除线
- `docs/overview/DataOwnership.md`：Qdrant/SiliconFlow 行标注已移除
- `docs/Integration/README.md`：Qdrant/SiliconFlow 行标注已移除
- `docs/modules/DocumentDeletion/`：5 个文件中 Qdrant 向量数据删除相关描述标注已移除
- `docs/modules/DocumentMetadata/`：4 个文件中 Qdrant 向量索引元数据更新相关描述标注已移除
- `docs/modules/DocumentParsing/`：6 个文件中 Qdrant 向量索引写入相关描述标注已移除
- `docs/modules/DocumentUpload/03-DESIGN.md`：IQdrantService 相关描述标注已移除
- `docs/modules/ExactSearch/`：01-FEATURE.md 和 04-TASKS.md 中 HybridSearch 关联描述标注已移除

