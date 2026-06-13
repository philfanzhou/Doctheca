[
  {
    "id": "T-01",
    "title": "代码审查：GetDocumentListAsync 分页参数修正",
    "type": "code-review",
    "description": "检查 DocumentDomainService.GetDocumentListAsync 中 page≤0 修正为 1、size≤0 修正为 20、size>100 修正为 100 的逻辑是否正确。",
    "target": "src/Domain/Services/DocumentDomainService.cs",
    "checkpoints": [
      "page <= 0 时修正为 1",
      "size <= 0 时修正为 20",
      "size > 100 时修正为 100",
      "修正逻辑在调用 Repository 之前执行"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-02",
    "title": "代码审查：ListDocuments 端点参数绑定",
    "type": "code-review",
    "description": "检查 DocumentAdminEndpoints.ListDocuments 的参数绑定是否正确：page/pageSize/status/subject/grade/keyword/year 全部为 [FromQuery] 可选参数，默认值合理。",
    "target": "src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "page 默认值为 1",
      "pageSize 默认值为 20",
      "status/subject/grade/keyword/year 默认值为 null",
      "所有参数均为 [FromQuery] 可选"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-03",
    "title": "代码审查：ListDocuments 返回 JSON 结构",
    "type": "code-review",
    "description": "检查 ListDocuments 返回的 JSON 是否包含 success、data（items 数组）、total、page、pageSize、totalPages 字段。",
    "target": "src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "返回 Results.Ok 包含 success=true",
      "data 为 items.Select 映射后的数组",
      "每条 item 包含 id/title/source_type/subject/grade/year/tags/status/created_at/updated_at",
      "tags 使用 JsonSerializer.Deserialize<string[]> 反序列化",
      "tags 为 null 时返回 null",
      "totalPages 计算公式为 (totalCount + pageSize - 1) / pageSize"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-04",
    "title": "代码审查：IDocumentRepository.GetListAsync 签名",
    "type": "code-review",
    "description": "检查 IDocumentRepository.GetListAsync 方法签名是否与 DocumentDomainService 调用一致，参数顺序和类型正确。",
    "target": "src/Domain/Repositories/IRepositories.cs",
    "checkpoints": [
      "返回类型为 Task<(List<DocumentModel> Items, int TotalCount)>",
      "参数顺序：page, size, status?, subject?, grade?, keyword?, year?",
      "筛选参数均为 string? 可选"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-05",
    "title": "代码审查：DocumentRepository.GetListAsync 实现",
    "type": "code-review",
    "description": "检查 DocumentRepository 中 GetListAsync 的 EF Core 实现：动态构建 Where 条件、CountAsync + Skip/Take 分页查询。",
    "target": "src/Database/Repositories/DocumentRepository.cs",
    "checkpoints": [
      "status 非空时添加 Where 条件",
      "subject 非空时添加 Where 条件",
      "grade 非空时添加 Where 条件",
      "keyword 非空时添加 Where 条件（标题包含）",
      "year 非空时添加 Where 条件",
      "先 CountAsync 再 Skip/Take",
      "Model 与 Entity 映射正确"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-06",
    "title": "编译验证：dotnet build 成功",
    "type": "build-verify",
    "description": "在 services/ruoyu.docretrieval 目录执行 dotnet build -c Release，确认零编译错误。",
    "target": "services/ruoyu.docretrieval",
    "checkpoints": [
      "dotnet build -c Release 退出码为 0",
      "无 CSxxxx 错误"
    ],
    "acceptance": "dotnet build -c Release"
  },
  {
    "id": "T-07",
    "title": "测试验证：运行全部单元测试",
    "type": "test-verify",
    "description": "运行文档列表相关的单元测试，确认全部通过。",
    "target": "services/ruoyu.docretrieval",
    "checkpoints": [
      "所有 [Fact] 方法全部通过",
      "退出码为 0"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentListTests\""
  },
  {
    "id": "T-08",
    "title": "文档一致性审查：FEATURE/SPEC/DESIGN 与代码一致",
    "type": "doc-review",
    "description": "对比本目录 FEATURE.md、SPEC.md、DESIGN.md 中的分页修正规则、返回结构、筛选条件是否与实际代码一致。",
    "target": "docs/modules/DocumentList/*.md vs src/Domain/Services/DocumentDomainService.cs + src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "分页修正规则与文档一致",
      "返回 JSON 结构与文档一致",
      "筛选条件列表与文档一致"
    ],
    "acceptance": "人工审查"
  }
]
