# DocumentDeletion — 任务与验证 (TASKS)

> 说明：DocumentDeletion 的生产代码已实现完成。以下任务面向代码审查、可测性验证与回归保障，每个任务都有一条可自动执行的命令或一组断言。

```json
[
  {
    "id": "T01",
    "depends_on": [],
    "action": "审查 DocumentDomainService.cs 中 DeleteDocumentAsync 方法：确认幂等逻辑（document == null 返回 true）、级联删除顺序（occurrences → questions → segments → pages → document → SaveChanges）、搜索索引清理（try/catch 容错）。",
    "files": [
      "src/Domain/Services/DocumentDomainService.cs"
    ],
    "acceptance": "dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln",
    "notes": "级联删除顺序必须严格按 occurrences → questions → segments → pages → document 执行。"
  },
  {
    "id": "T02",
    "depends_on": [],
    "action": "审查 DocumentAdminEndpoints.cs 中 DeleteDocument 方法：确认先获取文档信息、再删数据库、最后删 OSS 的执行顺序，以及 OSS 删除的 try/catch 容错和 Warning 日志。",
    "files": [
      "src/Service/DocumentAdminEndpoints.cs"
    ],
    "acceptance": "dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln",
    "notes": "OSS 删除失败仅记 Warning 日志，不影响接口返回。"
  },
  {
    "id": "T03",
    "depends_on": [],
    "action": "审查各仓储的 DeleteByDocumentIdAsync 实现：确认 DocumentOccurrenceRepository、QuestionSegmentRepository、DocumentSegmentRepository、DocumentPageRepository 均实现了按 documentId 批量删除。",
    "files": [
      "src/Database/Repositories/DocumentOccurrenceRepository.cs",
      "src/Database/Repositories/QuestionSegmentRepository.cs",
      "src/Database/Repositories/DocumentSegmentRepository.cs",
      "src/Database/Repositories/DocumentPageRepository.cs"
    ],
    "acceptance": "dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln",
    "notes": "无关联记录时删除操作不报错。"
  },
  {
    "id": "T04",
    "depends_on": [],
    "action": "审查 DocumentRepository.cs 中 GetByTitleAsync 和 DeleteAsync 实现：确认按标题查询和按 ID 删除的正确性。",
    "files": [
      "src/Database/Repositories/DocumentRepository.cs"
    ],
    "acceptance": "dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln",
    "notes": "GetByTitleAsync 在标题不存在时返回 null。"
  },
  {
    "id": "T05",
    "depends_on": [],
    "action": "审查 DeleteDocument 端点返回格式：确认返回 { success: true, data: { title, deleted: document != null } }，其中 deleted 字段反映文档是否实际存在过。",
    "files": [
      "src/Service/DocumentAdminEndpoints.cs"
    ],
    "acceptance": "dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln",
    "notes": "deleted=true 表示文档存在并被删除，deleted=false 表示文档不存在（幂等）。"
  },
  {
    "id": "T06",
    "depends_on": ["T01", "T02", "T03", "T04", "T05"],
    "action": "运行一次完整的回归测试：对 DocumentDeletion 相关测试执行 dotnet test 并确保全部通过。",
    "files": [
      "[当前无测试覆盖] tests/**/*DocumentDeletion*Tests.cs"
    ],
    "acceptance": "dotnet test --filter 'FullyQualifiedName~DocumentDeletion|FullyQualifiedName~DeleteDocument'",
    "notes": "若测试项目尚未创建，本任务同时包含'补齐 DocumentDeletionTests'子任务。"
  }
]
```

## 命令速查

在 `backend/ruoyu.docretrieval/` 目录下执行：

```bash
# 构建
dotnet build --no-restore Ruoyu.Study.DocRetrieval.sln

# 按命名空间筛选运行
dotnet test --filter "FullyQualifiedName~DocumentDeletion"

# 按具体方法筛选
dotnet test --filter "FullyQualifiedName~DeleteDocument"
```

## 任务依赖图

```
T01 ─┐
T02 ─┤
T03 ─┼─ T06
T04 ─┤
T05 ─┘
```
