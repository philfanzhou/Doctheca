# DocumentMetadataAnalysis — 测试文档

## 当前测试现状

**当前无单元测试覆盖。** `src/Tests/Ruoyu.Study.DocLibrary.Tests/` 目录下不存在 `DocumentAnalysisServiceTests.cs`,元分析相关功能目前零测试。项目测试集中在 `ConstantsTests` / `DocumentFileServiceTests` / `DocumentFileDeleteCleanupTests` / `DocumentParseBlockServiceTests` / `OpenSearchIndexServiceTests` 等文件,详见仓库测试目录。

> 本仓库采用 xUnit + Moq + FluentAssertions。新增测试需遵循 `.agent/rules/coding-policy.md` 对 `ruoyu.doclibrary` 项目的测试约定。

## 建议覆盖方向

以下为基于 `DocumentAnalysisService` 中 `internal` 可测方法的建议覆盖方向(需 `InternalsVisibleTo` 测试项目),均为纯逻辑测试,不依赖 LLM HTTP 或数据库。

### 单元测试方向 (建议通过 `InternalsVisibleTo` 访问 internal 方法)

| # | 覆盖方向 | 目标方法(测试入口) | 覆盖 FR |
|---|---------|-------------------|---------|
| T-01 | `textPreview` 为 null/whitespace → `AnalyzeMetadataAsync` 返回 null | `AnalyzeMetadataAsync` | FR-04, FR-05 |
| T-02 | `ChunkSize <= 0`(LLM 未初始化/禁用)→ 返回 null,无 LLM 调用 | `AnalyzeMetadataAsync` | FR-05 |
| T-03 | 有效 JSON 响应 → 正确 subject/grade/year | `ParseMetadataAnalysis` | FR-03, FR-04 |
| T-04 | JSON 中字段为空字符串 → 视为 null | `ParseMetadataAnalysis` | FR-04 |
| T-05 | JSON 中字段为空白 → 视为 null | `ParseMetadataAnalysis` | FR-04 |
| T-06 | JSON 中字段为 `"null"` 字符串字面量 → 视为 null | `ParseMetadataAnalysis` | FR-04 |
| T-07 | 无效 JSON → 返回 null,不抛异常 | `ParseMetadataAnalysis` | FR-05 |
| T-08 | markdown ```json``` / ```` ``` 代码块包装的 JSON 可正确解析 | `ParseMetadataAnalysis`(`ExtractJson`) | FR-04 |
| T-09 | 长文本(>2000 字)输入 → 截断到前 2000 字符 | `AnalyzeMetadataAsync` | FR-04 |
| T-10 | `BuildMetadataAnalysisPrompt` 包含学科/年级/年份说明关键字 | `BuildMetadataAnalysisPrompt` | FR-04 |
| T-11 | `BuildMetadataAnalysisPrompt` 不含拆段/分段策略相关词 | `BuildMetadataAnalysisPrompt` | FR-04 |
| T-12 | `ParseTokenCount("128K")` → 131072,`"1M"` → 1048576,纯数字 → 原值 | `ParseTokenCount` | 初始化逻辑 |

### 集成测试方向(规划)

| # | 覆盖方向 | 涉及组件 | 覆盖 AC |
|---|---------|---------|---------|
| IT-01 | 解析完成后元数据全有值 → Worker 调用 `GetService<IDocumentAnalysisService>` 后跳过 LLM | `MinerUFileParseWorker` + `IDocumentFileService` | AC-03 |
| IT-02 | 解析完成后元数据缺失 → 调用 LLM 填充,DB 更新,OpenSearch 同步 | `MinerUFileParseWorker` + `DocumentAnalysisService`(mock) | AC-04 |
| IT-03 | LLM 调用失败 → 解析状态仍为 `parsed`,OpenSearch 索引已写入 | `MinerUFileParseWorker` | AC-05 |
| IT-04 | `IDocumentAnalysisService` 未注册(ApiKey 为空)→ Worker 跳过,记 Info 日志 | `MinerUFileParseWorker`(无 DI 注册) | AC-06 |
| IT-05 | 手动 PUT 元数据 → DB 更新 + OpenSearch `UpdateByQueryAsync` 同步 | `DocumentFileEndpoints` + `OpenSearchIndexService` | AC-07 |
| IT-06 | LLM 分析时不覆盖已有字段(保留 DB 原值) | `MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync` | AC-08 |

## 运行方式

```bash
# 运行所有测试(目前无 DocumentAnalysis 专项测试)
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --configuration Release

# 按现有测试文件筛选(示例)
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~OpenSearchIndexServiceTests"
```

## 覆盖率目标(待 DM-18 实施)

- `DocumentAnalysisService` 代码行覆盖率 ≥ 80%
- 纯逻辑方法 `BuildMetadataAnalysisPrompt` / `ParseMetadataAnalysis` / `ParseTokenCount` 100% 覆盖
