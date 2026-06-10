# HybridSearch — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 DocumentRetrievalServiceImpl.cs 中 HybridSearch 方法：确认参数校验（与 ExactSearch 共用 ValidateSearchRequest）、exactTopK/semanticTopK 默认值和上限修正、pageSize 修正、MapFilter 空字符串转 null 逻辑与 SPEC 一致。",
    "files": ["src/Service/DocumentRetrievalServiceImpl.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "exactTopK 默认 50 最大 200；semanticTopK 默认 20 最大 100。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 SearchDomainService.cs 中 HybridSearchAsync 方法：确认 OpenSearch 优先调用、异常捕获后 LogWarning 回退 DatabaseSearchAsync、回退时 exact_word → stem_match 降级逻辑与 SPEC 一致。",
    "files": ["src/Domain/Services/SearchDomainService.cs"],
    "acceptance": "编译通过；回退路径有 LogWarning 日志；MatchType 降级逻辑正确。",
    "notes": "降级逻辑：foreach results, if MatchType==\"exact_word\" then set to \"stem_match\"。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 OpenSearchIndexService.cs 中 HybridSearchAsync 方法：确认精确结果调用 ExactSearchAsync、语义结果调用 IQdrantService.SemanticSearchAsync、Qdrant 异常降级、合并去重逻辑（DocumentName|PageNumber|SegmentId）、排序优先级（exact_phrase > exact_word > stemmed > semantic）。",
    "files": ["src/Service/OpenSearchIndexService.cs"],
    "acceptance": "编译通过；合并去重和排序逻辑与 SPEC 一致。",
    "notes": "IQdrantService 通过 IServiceProvider.GetService 可选获取。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 IQdrantService.cs 接口定义：确认 SemanticSearchAsync 方法签名（query, topK, filter）和返回类型 List<SearchResultModel>。",
    "files": ["src/Domain/Repositories/IQdrantService.cs"],
    "acceptance": "接口签名与 OpenSearchIndexService 调用一致。",
    "notes": ""
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01"],
    "action": "执行参数校验测试：验证 HybridSearch 与 ExactSearch 共享相同的查询词校验（空查询、长度超限、page_size 超限）。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearch_InvalidArgument",
    "notes": "三个错误码与 ExactSearch 相同。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["REVIEW-01"],
    "action": "执行 exactTopK/semanticTopK 默认值和上限测试：验证 exactTopK=0 → 50、exactTopK=300 → 200、semanticTopK=0 → 20、semanticTopK=150 → 100。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearch_TopKDefaults",
    "notes": ""
  },
  {
    "id": "TEST-03",
    "depends_on": ["REVIEW-02"],
    "action": "执行 OpenSearch 回退 + MatchType 降级测试：Mock ISearchIndexService.HybridSearchAsync 抛异常，验证回退 DatabaseSearchAsync 并将 exact_word 降级为 stem_match。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearchAsync_FallbackWithMatchTypeDowngrade",
    "notes": "同时测试 _searchIndexService 为 null 时的直接回退路径。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["REVIEW-03"],
    "action": "执行 OpenSearch 混合搜索合并去重测试：构造精确结果和语义结果中有重复 DocumentName+PageNumber+SegmentId 的数据，验证精确结果优先保留。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/OpenSearchIndexServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearchAsync_MergeDeduplication",
    "notes": ""
  },
  {
    "id": "TEST-05",
    "depends_on": ["REVIEW-03"],
    "action": "执行混合搜索排序优先级测试：构造不同 MatchType 的结果（exact_phrase、exact_word、stemmed、semantic），验证排序优先级正确。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/OpenSearchIndexServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearchAsync_SortPriority",
    "notes": "同优先级按 Score 降序。"
  },
  {
    "id": "TEST-06",
    "depends_on": ["REVIEW-03"],
    "action": "执行 Qdrant 降级测试：Mock IQdrantService.SemanticSearchAsync 抛异常，验证仅返回精确搜索结果并 LogWarning。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/OpenSearchIndexServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~HybridSearchAsync_QdrantFallback",
    "notes": "IQdrantService 为 null 时也应仅返回精确结果。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04", "TEST-05", "TEST-06"],
    "action": "在 Release 配置下编译解决方案并运行所有测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.docretrieval/*.sln", "backend/ruoyu.docretrieval/src/**/*.cs", "backend/ruoyu.docretrieval/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~HybridSearch",
    "notes": "必须零警告，无测试失败。"
  }
]
```
