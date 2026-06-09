[
  {
    "id": "T-01",
    "title": "代码审查：UpdateMetadataAsync 文档查找与状态校验",
    "type": "code-review",
    "description": "检查 DocumentDomainService.UpdateMetadataAsync 中按标题查找文档的逻辑，以及文档不存在和未就绪时的异常抛出。",
    "target": "src/Domain/Services/DocumentDomainService.cs",
    "checkpoints": [
      "使用 GetByTitleAsync(title) 查找文档",
      "null 时抛 DocRetrievalValidationException(\"文档不存在\")",
      "Status != \"ready\" 时抛 DocRetrievalValidationException(\"文档未就绪，不允许修改元数据\")",
      "状态校验在学科/年级校验之前"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-02",
    "title": "代码审查：UpdateMetadataAsync 学科与年级校验",
    "type": "code-review",
    "description": "检查学科校验使用 DocRetrievalConstants.IsValidSubject，年级校验使用 DocRetrievalConstants.IsValidGrade，且仅在参数非 null 时校验。",
    "target": "src/Domain/Services/DocumentDomainService.cs",
    "checkpoints": [
      "subject != null 时调用 IsValidSubject(subject)",
      "grade != null 时调用 IsValidGrade(grade)",
      "学科无效消息为\"学科仅支持：英语\"",
      "年级无效消息包含有效值列表",
      "校验在字段赋值之前"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-03",
    "title": "代码审查：UpdateMetadataAsync 字段更新与时间戳",
    "type": "code-review",
    "description": "检查仅非 null 字段被更新，UpdatedAt 设置为 DateTimeOffset.UtcNow。",
    "target": "src/Domain/Services/DocumentDomainService.cs",
    "checkpoints": [
      "subject != null 时赋值 document.Subject",
      "grade != null 时赋值 document.Grade",
      "year != null 时赋值 document.Year",
      "tags != null 时赋值 document.Tags",
      "document.UpdatedAt = DateTimeOffset.UtcNow",
      "UpdatedAt 在所有字段赋值之后设置"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-04",
    "title": "代码审查：搜索索引同步与异常隔离",
    "type": "code-review",
    "description": "检查 _searchIndexService 非空时调用 UpdateDocumentMetadataAsync，异常被 try/catch 捕获仅记 LogError。",
    "target": "src/Domain/Services/DocumentDomainService.cs",
    "checkpoints": [
      "_searchIndexService != null 判断存在",
      "调用 UpdateDocumentMetadataAsync(document.Id, document.Subject, document.Grade, document.Year)",
      "try/catch 包裹索引同步调用",
      "catch 块仅 LogError，不 throw",
      "LogError 包含异常对象和文档标题"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-05",
    "title": "代码审查：UpdateMetadata 端点 JSON 解析与 tags 获取",
    "type": "code-review",
    "description": "检查端点中 JSON body 解析逻辑，tags 使用 GetRawText() 获取，全部为 null 时返回 400。",
    "target": "src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "使用 JsonSerializer.Deserialize<JsonElement> 解析 body",
      "subject 使用 GetString() 获取",
      "grade 使用 GetString() 获取",
      "year 使用 GetString() 获取",
      "tags 使用 GetRawText() 获取",
      "四个字段全为 null 时返回 400 \"至少提供一项元数据\""
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-06",
    "title": "代码审查：UpdateMetadata 端点错误码映射",
    "type": "code-review",
    "description": "检查 DocRetrievalValidationException 的消息匹配与 HTTP 状态码映射：不存在→404, 未就绪→422, 学科无效→400, 年级无效→400。",
    "target": "src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "消息包含\"不存在\" → 404 DOCRETRIEVAL_DOCUMENT_NOT_FOUND",
      "消息包含\"未就绪\" → 422 DOCRETRIEVAL_DOCUMENT_NOT_READY",
      "消息包含\"学科仅支持\" → 400 DOCRETRIEVAL_SUBJECT_INVALID",
      "消息包含\"年级取值非法\" → 400 DOCRETRIEVAL_GRADE_INVALID",
      "使用 Results.Json 返回带 statusCode 的响应"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-07",
    "title": "代码审查：UpdateMetadata 端点成功响应结构",
    "type": "code-review",
    "description": "检查成功更新后返回的 JSON 结构包含 id/title/subject/grade/year/tags 字段。",
    "target": "src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "返回 Results.Ok 包含 success=true",
      "data 包含 id/title/subject/grade/year/tags",
      "tags 使用 JsonSerializer.Deserialize<string[]> 反序列化",
      "tags 为 null 时返回 null"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-08",
    "title": "编译验证：dotnet build 成功",
    "type": "build-verify",
    "description": "在 backend/ruoyu.docretrieval 目录执行 dotnet build -c Release，确认零编译错误。",
    "target": "backend/ruoyu.docretrieval",
    "checkpoints": [
      "dotnet build -c Release 退出码为 0",
      "无 CSxxxx 错误"
    ],
    "acceptance": "dotnet build -c Release"
  },
  {
    "id": "T-09",
    "title": "测试验证：运行全部单元测试",
    "type": "test-verify",
    "description": "运行文档元数据更新相关的单元测试，确认全部通过。",
    "target": "backend/ruoyu.docretrieval",
    "checkpoints": [
      "所有 [Fact] 方法全部通过",
      "退出码为 0"
    ],
    "acceptance": "dotnet test --filter \"FullyQualifiedName~DocumentMetadataTests\""
  },
  {
    "id": "T-10",
    "title": "文档一致性审查：FEATURE/SPEC/DESIGN 与代码一致",
    "type": "doc-review",
    "description": "对比本目录 FEATURE.md、SPEC.md、DESIGN.md 中的校验规则、错误码映射、tags 格式是否与实际代码一致。",
    "target": "docs/modules/DocumentMetadata/*.md vs src/Domain/Services/DocumentDomainService.cs + src/Service/DocumentAdminEndpoints.cs",
    "checkpoints": [
      "异常消息与文档一致",
      "错误码映射与文档一致",
      "tags 使用 GetRawText() 与文档一致",
      "搜索索引同步异常隔离与文档一致"
    ],
    "acceptance": "人工审查"
  }
]
