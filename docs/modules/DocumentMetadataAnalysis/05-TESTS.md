# DocumentMetadataAnalysis — 测试文档

## 单元测试（DocumentAnalysisServiceTests.cs）

| # | 测试方法 | 覆盖 | 状态 |
|---|---------|------|------|
| UT-DM-01 | `AnalyzeMetadataAsync_EmptyText_ReturnsNull` | FR-04, FR-05 | completed |
| UT-DM-02 | `AnalyzeMetadataAsync_ValidResponse_ReturnsMetadata` | FR-03, FR-04 | completed |
| UT-DM-03 | `AnalyzeMetadataAsync_NullFieldsInResponse_ReturnsNullFields` | FR-04 | completed |
| UT-DM-04 | `AnalyzeMetadataAsync_EmptyStringFieldsInResponse_ReturnsNullFields` | FR-04 | completed |
| UT-DM-05 | `AnalyzeMetadataAsync_NullStringLiteralInResponse_ReturnsNullFields` | FR-04 | completed |
| UT-DM-06 | `AnalyzeMetadataAsync_InvalidJson_ReturnsNull` | FR-05 | completed |
| UT-DM-07 | `AnalyzeMetadataAsync_LlmCallFails_ReturnsNull` | FR-05 | completed |
| UT-DM-08 | `AnalyzeMetadataAsync_LongText_TruncatesTo2000Chars` | FR-04 | completed |
| UT-DM-09 | `BuildMetadataAnalysisPrompt_ContainsSubjectInstructions` | FR-04 | completed |
| UT-DM-10 | `BuildMetadataAnalysisPrompt_ContainsGradeInstructions` | FR-04 | completed |
| UT-DM-11 | `BuildMetadataAnalysisPrompt_ContainsYearInstructions` | FR-04 | completed |
| UT-DM-12 | `BuildMetadataAnalysisPrompt_DoesNotContainSegmentationStrategy` | FR-04 | completed |
| UT-DM-13 | `BuildMetadataAnalysisPrompt_ContainsTextInput` | FR-04 | completed |
| UT-DM-14 | `ParseMetadataAnalysis_MarkdownCodeBlockWrapper_ParsesCorrectly` | FR-04 | completed |
| UT-DM-15 | `ParseMetadataAnalysis_PartialFields_PreservesNulls` | FR-04 | completed |

## 集成测试（规划中）

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-DM-01 | 解析完成后元数据全有值时不调用 LLM | AC-03 |
| IT-DM-02 | 解析完成后元数据缺失时调用 LLM 填充 | AC-04 |
| IT-DM-03 | LLM 失败不影响解析状态 | AC-05 |
| IT-DM-04 | LLM 未配置时跳过分析 | AC-06 |
| IT-DM-05 | 手动设置元数据后 OpenSearch 同步刷新 | AC-07 |
| IT-DM-06 | LLM 不覆盖已有元数据 | AC-08 |

## 运行方式

```bash
# 运行本模块单元测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentAnalysis"

# 运行所有测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests
```

## 覆盖率目标

- `DocumentAnalysisService` 代码行覆盖率 ≥ 80%
- 纯逻辑方法（`BuildMetadataAnalysisPrompt`、`ParseMetadataAnalysis`、`ParseTokenCount`）100% 覆盖
