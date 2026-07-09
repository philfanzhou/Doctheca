# OpenSearchBlockIndexing — 测试文档

## 单元测试（OpenSearchIndexServiceTests.cs）

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-OBI-01 | `EnsureIndexAsync_IndexDoesNotExist_CreatesIndex` | EnsureIndexAsync |
| UT-OBI-02 | `EnsureIndexAsync_IndexAlreadyExists_SkipsCreation` | EnsureIndexAsync |
| UT-OBI-03 | `BuildIndexBody_ContainsCorrectSettings` | 分析器配置 |
| UT-OBI-04 | `BuildIndexBody_ContainsBlockFields` | mapping 字段 |
| UT-OBI-05 | `BuildSearchBody_PhraseQuery_UsesExactField` | phrase 查询 |
| UT-OBI-06 | `BuildSearchBody_WordQuery_UsesTextField` | 单词查询 |
| UT-OBI-07 | `BuildSearchBody_FilterClauses_IncludesSubjectGradeYear` | 过滤条件 |
| UT-OBI-08 | `ParseSearchResponse_WithBlockFields_ReadsBlockIdAndFileName` | 响应解析 |
| UT-OBI-09 | `ParseSearchResponse_HighlightedText_UsedAsAssociatedText` | 高亮文本 |
| UT-OBI-10 | `BuildIndexBody_RetainsSharedFieldsAndAnalyzers` | 字段兼容性 |

## 集成测试（规划中）

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-OBI-01 | MinerU 解析完成后 OpenSearch 有 blocks 数据 | AC-01 |
| IT-OBI-02 | 同一文档多次解析，OpenSearch 有多份 blocks | AC-02 |
| IT-OBI-03 | 删除 parse 后 OpenSearch 无该 parse 数据 | AC-03 |
| IT-OBI-04 | 删除文档后 OpenSearch 无该文档数据 | AC-04 |
| IT-OBI-05 | 搜索能命中 blocks 数据 | AC-05 |
| IT-OBI-06 | 搜索响应字段兼容前端 | AC-06 |
| IT-OBI-07 | OpenSearch 不可用时索引/删除 best-effort | NFR-04 |

## 运行方式

```bash
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~OpenSearch"
```
