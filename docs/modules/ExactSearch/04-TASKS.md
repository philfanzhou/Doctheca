# ExactSearch — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 DocumentRetrievalServiceImpl.cs 中 ExactSearch 方法：确认参数校验逻辑（query 为空/过长、page_size 超限）、pageSize 默认值和上限修正、MapFilter 空字符串转 null 逻辑与 SPEC 一致。",
    "files": ["src/Service/DocumentRetrievalServiceImpl.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "ValidateSearchRequest 方法被 ExactSearch 共用。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 SearchDomainService.cs 中 ExactSearchAsync 方法：确认 OpenSearch 优先调用、异常捕获后 LogWarning 回退 DatabaseSearchAsync 的逻辑与 SPEC 一致。",
    "files": ["src/Domain/Services/SearchDomainService.cs"],
    "acceptance": "编译通过；回退路径有 LogWarning 日志。",
    "notes": "_searchIndexService 通过 IServiceProvider.GetService 可选获取，可能为 null。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 SearchDomainService.cs 中 DatabaseSearchAsync 方法：确认 segments + questions 两表 IndexOf(query, OrdinalIgnoreCase) 匹配、status==ready 过滤、短语/单词匹配的 Score 和 MatchType 设置、去重逻辑、游标分页编码/解码。",
    "files": ["src/Domain/Services/SearchDomainService.cs"],
    "acceptance": "编译通过；所有 SPEC 验收点均有覆盖。",
    "notes": "去重键为 DocumentName|PageNumber|SegmentId；page_token 为 Base64(JSON({skip:N}))。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 OpenSearchIndexService.cs 中 ExactSearchAsync 方法：确认短语查询使用 match_phrase + text.exact 字段、单词查询使用 match + text 字段、filter 构建 term 子句、search_after 游标分页、highlight 高亮。",
    "files": ["src/Service/OpenSearchIndexService.cs"],
    "acceptance": "编译通过；查询构建逻辑与 SPEC 一致。",
    "notes": "text.exact 使用 english_phrase 分析器（仅小写归一），text 使用 english_custom 分析器（含词干提取）。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03"],
    "action": "执行参数校验测试：验证查询词为空、查询词超过 200 字符、page_size 超过 100 时均抛出 RpcException(StatusCode.InvalidArgument)。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ExactSearch_InvalidArgument",
    "notes": "三个错误码：DOCRETRIEVAL_QUERY_REQUIRED、DOCRETRIEVAL_QUERY_TOO_LONG、DOCRETRIEVAL_PAGE_SIZE_INVALID。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["REVIEW-02"],
    "action": "执行 OpenSearch 回退测试：Mock ISearchIndexService.ExactSearchAsync 抛异常，验证 SearchDomainService 回退 DatabaseSearchAsync 并记录 LogWarning。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ExactSearchAsync_FallbackToDatabase",
    "notes": "同时测试 _searchIndexService 为 null 时的直接回退路径。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["REVIEW-03"],
    "action": "执行数据库搜索匹配测试：验证 segments 和 questions 两表的 IndexOf 匹配、phrase=true 时 Score=1.0/MatchType=exact_phrase、phrase=false 时 Score=0.8/MatchType=exact_word。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~DatabaseSearchAsync",
    "notes": ""
  },
  {
    "id": "TEST-04",
    "depends_on": ["REVIEW-03"],
    "action": "执行去重和分页测试：验证 DocumentName+PageNumber+SegmentId 去重保留首条、游标分页的 skip/take 逻辑、next_token 编码/解码。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/SearchDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ExactSearch_DeduplicationAndPagination",
    "notes": "page_token 解码失败时 skip 应默认为 0。"
  },
  {
    "id": "TEST-05",
    "depends_on": ["REVIEW-01"],
    "action": "执行 MapFilter 测试：验证 SearchFilter 空字符串字段转为 null、非空字段保留原值。",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentRetrievalServiceImplTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~MapFilter",
    "notes": ""
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04", "TEST-05"],
    "action": "在 Release 配置下编译解决方案并运行所有测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.docretrieval/*.sln", "backend/ruoyu.docretrieval/src/**/*.cs", "backend/ruoyu.docretrieval/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~ExactSearch",
    "notes": "必须零警告，无测试失败。"
  }
]
```
