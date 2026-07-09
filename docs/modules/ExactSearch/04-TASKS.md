# ExactSearch — 任务清单 (TASKS)

> **本模块代码已实现完成，以下任务为代码评审与自动化验证记录。**

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 DocumentSearchEndpoints.cs 中 Search 方法：确认参数校验逻辑（query 为空/过长、pageSize 超限静默截断）、pageSize 默认值和上限修正、MapFilter 空字符串转 null 逻辑与 SPEC 一致。",
    "files": ["src/Service/Endpoints/DocumentSearchEndpoints.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "ValidateSearchRequest 方法被 ExactSearch 共用。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 SearchDomainService.cs 中 ExactSearchAsync 方法：确认 OpenSearch 调用、异常捕获后 LogWarning 返回空结果的逻辑与 SPEC 一致。",
    "files": ["src/Domain/Services/SearchDomainService.cs"],
    "acceptance": "编译通过；不可用路径有 LogWarning 日志并返回空结果。",
    "notes": "_searchIndexService 通过 IServiceProvider.GetService 可选获取，可能为 null。"
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
    "depends_on": ["REVIEW-01"],
    "action": "执行参数校验测试：验证查询词为空、查询词超过 200 字符时返回 HTTP 400 Bad Request；pageSize 超过 100 时静默截断。",
    "files": ["test/Ruoyu.Study.DocLibrary.Tests/DocumentSearchEndpointsTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ExactSearch_InvalidArgument",
    "notes": "两个错误码：DOCLIBRARY_QUERY_REQUIRED、DOCLIBRARY_QUERY_TOO_LONG。pageSize 超限不再报错，静默截断为 100。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["REVIEW-02"],
    "action": "执行 OpenSearch 不可用测试：Mock ISearchIndexService.ExactSearchAsync 抛异常，验证 SearchDomainService 返回空结果并记录 LogWarning。",
    "files": ["test/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ExactSearchAsync_Unavailable",
    "notes": "同时测试 _searchIndexService 为 null 时的直接返回空结果路径。"
  },
  {
    "id": "TEST-05",
    "depends_on": ["REVIEW-01"],
    "action": "执行 MapFilter 测试：验证 SearchFilter 空字符串字段转为 null、非空字段保留原值。",
    "files": ["test/Ruoyu.Study.DocLibrary.Tests/DocumentSearchEndpointsTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~MapFilter",
    "notes": ""
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-05"],
    "action": "在 Release 配置下编译解决方案并运行所有测试，打印覆盖率摘要。",
    "files": ["src/services/ruoyu.doclibrary/*.sln", "src/services/ruoyu.doclibrary/src/**/*.cs", "src/services/ruoyu.doclibrary/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~ExactSearch",
    "notes": "必须零警告，无测试失败。"
  }
]
```
