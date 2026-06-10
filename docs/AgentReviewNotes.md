# Agent Review Notes

## 1. 本轮任务范围

对 `ruoyu.docretrieval` 服务的 `docs/` 目录进行结构优化：将根目录散落的 7 份总览文档收拢到 `docs/overview/`，根目录仅保留薄入口 `README.md` 和过程性文档 `AgentReviewNotes.md`。

## 2. 已完成的文档调整

### 2.1 结构调整

| 操作 | 说明 |
|------|------|
| 新增 `docs/overview/` 目录 | 收拢服务级总览文档 |
| 迁移 7 份总览文档 | `SystemContext.md`、`Integration.md`、`KeyFlows.md`、`DataOwnership.md`、`Requirements.md`、`Design.md`、`DotNetCodingPolicy.md` → `docs/overview/` |
| 新增 `docs/overview/README.md` | 总览文档索引 |
| 重写 `docs/README.md` | 从详细导航改为薄根入口，引导至 `overview/` |

### 2.2 链接修复

| # | 文件 | 修正内容 |
|---|------|---------|
| 1 | `docs/README.md` | 所有总览文档链接从 `./X.md` → `./overview/X.md` |
| 2 | `docs/Integration/README.md` | `../Integration.md` → `../overview/Integration.md` |
| 3 | `docs/overview/Requirements.md` | 9 个 `modules/` 链接 → `../modules/` |
| 4 | `docs/overview/Integration.md` | 2 个 `../src/` 链接 → `../../src/` |
| 5 | `docs/overview/Design.md` | 8 个 `../src/` 链接 → `../../src/` |
| 6 | `docs/overview/DotNetCodingPolicy.md` | `../../ruoyu.mistake/` → `../../../ruoyu.mistake/` |

### 2.3 历史修正记录（前几轮已完成，保留备查）

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

## 3. 待人工审核事项

### 3.1 日志和注释语言不一致

- **编号**：REV-01
- **问题描述**：DotNetCodingPolicy 4.4 节要求"注释和字符串必须使用英文"，但 DocRetrieval 服务代码中大量使用中文日志和中文异常消息
- **已查阅的证据**：`DocumentDomainService.cs`、`DocumentAdminEndpoints.cs`、`IngestionWorker.cs` 中的 ILogger 调用
- **为什么仍无法完全确认**：不确定这是有意为之（面向中文开发团队）还是遗漏
- **建议复核方式**：确认团队是否统一接受中文日志，如是则更新 DotNetCodingPolicy 的例外说明

### 3.2 DocumentDeletion 权限控制缺失

- **编号**：REV-02
- **问题描述**：`DocumentDeletion/02-SPEC.md` 中 REQ-DEL-08 写"仅管理员可调用此接口"，但代码中 `DeleteDocument` 端点无任何授权检查
- **已查阅的证据**：`DocumentAdminEndpoints.cs` 的 `DeleteDocument` 方法无 `[Authorize]` 特性
- **为什么仍无法完全确认**：可能由上层 API 网关统一处理鉴权
- **建议复核方式**：确认 API 网关是否对所有 `/admin/` 路径做了鉴权

### 3.3 HybridSearch 中 `stemmed` MatchType

- **编号**：REV-03
- **问题描述**：`stemmed` 作为 MatchType 值仅存在于优先级映射字典中，从未被代码路径实际产生
- **已查阅的证据**：`OpenSearchIndexService.cs` 的 `matchTypePriority` 字典和 `ExactSearchAsync` 方法
- **建议复核方式**：确认 `stemmed` 是否为未来功能预留

## 4. 证据不足但已落盘的内容

| 内容 | 文件 | 标注 |
|------|------|------|
| DotNetCodingPolicy 中"注释必须英文"与实际代码中文日志的矛盾 | `docs/overview/DotNetCodingPolicy.md` | `[推断]` |
| `source_type` 字段的 `word`/`ppt` 值来自代码常量推断 | `docs/database/tables/documents.md` | `[推断]` |

## 5. 风险与后续建议

1. **测试覆盖缺口**：IngestionWorker、DocumentDeletion、OpenSearchIndexService 均无单元测试
2. **文档与代码同步机制**：建议在 CI 中加入文档链接检查步骤
3. **状态枚举硬编码**：建议统一为枚举常量
4. **DocumentMetadata 测试骨架**：缺少 `Mock<IQdrantService>` 注入
