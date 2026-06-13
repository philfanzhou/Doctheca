# Agent Review Notes

## 1. 本轮任务范围

对 `ruoyu.docretrieval` 服务的 `docs/` 目录进行全面审查和优化：以代码为唯一事实源，修正文档中的错误、补齐缺失文档、统一格式和语言。

## 2. 已完成的文档调整

### 2.1 本轮修正

| # | 文件 | 修正内容 |
|---|------|---------|
| 1 | `overview/KeyFlows.md` | 状态值 `parsing` → `processing`（2 处），`completed` → `success`（1 处） |
| 2 | `development/verification.md` | 状态值 `completed` → `ready`/`success`；全文翻译为中文；`subject=Mathematics` → `subject=英语`（2 处） |
| 3 | `development/README.md` | 全文翻译为中文 |
| 4 | `development/local-setup.md` | 全文翻译为中文 |

### 2.2 本轮补齐

| # | 文件 | 说明 |
|---|------|------|
| 1 | `modules/DocumentList/02-SPEC.md` | 新建：9 条 FR + 9 组 AC（Given-When-Then） |
| 2 | `modules/DocumentDeletion/01-FEATURE.md` | 新建：核心用户故事 + 8 条 AC + 补充约束 |

### 2.3 核对结果

| 项目 | 结果 |
|------|------|
| database/ 6 张表文档 vs DatabaseInitializer.cs DDL | 完全一致，无需修改 |
| database/relations.md | 与 DDL 外键关系一致 |
| database/migrations.md | 与实际迁移策略一致（SQL-based 初始化，无 EF Migration） |
| overview/SystemContext.md | 与 Program.cs 注册代码一致 |
| overview/Integration.md | 与实际接口和降级策略一致 |
| overview/Design.md | 与项目分层和技术栈一致 |
| overview/DataOwnership.md | 与 DbContext DbSet 属性一致 |
| overview/Requirements.md | 与 modules/ 功能点对应一致 |

### 2.4 历史修正记录（前几轮已完成，保留备查）

- Integration.md HTTP 路径 `/api/` → `/admin/`，参数 `{id}` → `{title}`
- Requirements.md 模块链接修正（双层 → 单层）
- database/tables 状态值 `parsing` → `processing`，`completed` → `success`
- DocumentUpload 拼写错误 `DOCRETREIVAL_` → `DOCRETRIEVAL_`（62 处）
- DocumentParsing 轮询间隔 10s → 5s
- DocumentMetadata 补充 Qdrant 同步
- 各模块测试路径和命名空间修正
- modules/README.md 断链修复
- database/README.md 去重 ER 图
- development/ 目录创建（local-setup + verification）
- 根目录总览文档收拢到 `overview/`

## 3. 待人工审核事项

### 3.1 日志和注释语言不一致 ✅ 已整改

- **编号**：REV-01
- **问题描述**：DotNetCodingPolicy 4.4 节要求"注释和字符串必须使用英文"，但 DocRetrieval 服务代码中大量使用中文日志和中文异常消息
- **人工批注**：不是，需要修改代码或者日志。
- **整改结果**：已将全部 C# 源代码中的中文日志、异常消息、注释改为英文（9 个文件，约 100 处修改）。仅保留数据库域值常量（如 `SubjectEnglish = "英语"`）为中文。已更新 `docs/overview/DotNetCodingPolicy.md` 补充表。`dotnet build` 通过，104 个测试全部通过。

### 3.2 DocumentDeletion 权限控制缺失 ✅ 已整改

- **编号**：REV-02
- **问题描述**：`DocumentDeletion/02-SPEC.md` 中 REQ-DEL-08 写"仅管理员可调用此接口"，但代码中 `DeleteDocument` 端点无任何授权检查
- **人工批注**：是，这是设计意图。部署阶段会控制只有内网才能访问管理界面
- **整改结果**：已更新 `docs/modules/DocumentDeletion/02-SPEC.md` REQ-DEL-08 和 §4.2 安全章节，明确说明权限控制由部署层网络隔离实现。已更新 `docs/modules/DocumentDeletion/01-FEATURE.md` "范围外"章节。

### 3.3 HybridSearch 中 `stemmed` MatchType ✅ 已整改

- **编号**：REV-03
- **问题描述**：`stemmed` 作为 MatchType 值仅存在于优先级映射字典中，从未被代码路径实际产生
- **人工批注**：不是未来预留，这是要求实现的功能
- **整改结果**：已将 `OpenSearchIndexService.ExactSearchAsync` 中非短语搜索的 MatchType 从 `"exact_word"` 改为 `"stemmed"`（因为 `english_custom` 分析器含词干提取）。已将 `SearchDomainService.HybridSearchAsync` 数据库回退中的 `"stem_match"` 统一为 `"stemmed"`。已同步更新 `docs/modules/ExactSearch/02-SPEC.md`、`docs/modules/HybridSearch/02-SPEC.md`。`dotnet build` 通过，104 个测试全部通过。

### 3.4 DocumentList 缺少 02-SPEC.md 的历史原因

- **编号**：REV-04
- **问题描述**：DocumentList 模块之前缺少 02-SPEC.md，本轮已补齐，但需确认是否有其他模块也存在类似缺失
- **已查阅的证据**：Glob 扫描 `docs/modules/` 下所有文件
- **当前状态**：已补齐，7 个模块均有完整的 6 件套（部分模块的 01-FEATURE.md 是本轮新建）

## 4. 证据不足但已落盘的内容

| 内容 | 文件 | 标注 |
|------|------|------|
| DotNetCodingPolicy 中"注释必须英文"与实际代码中文日志的矛盾 | `docs/overview/DotNetCodingPolicy.md` | `[推断]` → **已解决**：代码已改为英文，补充表已更新 |
| `source_type` 字段的 `word`/`ppt` 值来自代码常量推断 | `docs/database/tables/documents.md` | `[推断]` |
| DocumentList 02-SPEC 中"无效筛选值返回空列表" | `docs/modules/DocumentList/02-SPEC.md` | `[推断]` |

## 5. 风险与后续建议

1. **测试覆盖缺口**：IngestionWorker、DocumentDeletion、OpenSearchIndexService 均无单元测试
2. **文档与代码同步机制**：建议在 CI 中加入文档链接检查步骤
3. **状态枚举硬编码**：建议统一为枚举常量
4. **DocumentMetadata 测试骨架**：缺少 `Mock<IQdrantService>` 注入 — **已移除**（2026-06-12，Qdrant 已移除）
5. **PPT 解析测试**：DocumentParserServiceTests 中未见 PPT 解析的测试用例

---

## 6. 语义搜索功能移除记录（2026-06-12）

HybridSearch（语义搜索）功能已于 2026-06-12 移除。本项目当前仅支持精确搜索（ExactSearch），不再依赖 Qdrant 和 Embedding API（SiliconFlow）。

受影响的文档已全部标注"已移除"，具体包括：
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
