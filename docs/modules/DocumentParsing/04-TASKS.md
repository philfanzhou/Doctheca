# 04-TASKS — DocumentParsing 任务列表

## 任务列表

```json
[
  {
    "id": "TASK-001",
    "title": "IngestionWorker 后台轮询机制",
    "description": "实现 BackgroundService，每 5 秒轮询 pending 任务，使用 CreateScope 获取 Scoped 服务",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-01",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": []
  },
  {
    "id": "TASK-002",
    "title": "任务状态管理（Start/Complete/Fail）",
    "description": "在 DocumentDomainService 中实现 StartIngestionJobAsync、CompleteIngestionJobAsync、FailIngestionJobAsync，同步更新 Job 和 Document 状态",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-02",
    "files": ["src/Domain/Services/DocumentDomainService.cs"],
    "depends_on": []
  },
  {
    "id": "TASK-003",
    "title": "IDocumentParserService 接口定义",
    "description": "定义解析器接口 ParseAsync(Stream, sourceType, CancellationToken)，返回 ParsedDocument",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-03",
    "files": ["src/Domain/Repositories/IDocumentParserService.cs"],
    "depends_on": []
  },
  {
    "id": "TASK-004",
    "title": "ParsedDocument 领域模型定义",
    "description": "定义 ParsedDocument、ParsedPage、ParsedSegment、ParsedQuestion、ParsedToken 模型",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-03",
    "files": ["src/Domain/Models/DocumentModels.cs"],
    "depends_on": []
  },
  {
    "id": "TASK-005",
    "title": "DocumentParserService 实现（PDF/Word/PPT）",
    "description": "实现 PDF（PdfPig）、Word（OpenXml）、PPT（OpenXml）三种格式的解析，包含 OCR 后处理、句子边界识别、题目边界识别、Token 分词与 Porter 词干还原",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-03",
    "files": ["src/Service/DocumentParserService.cs"],
    "depends_on": ["TASK-003", "TASK-004"]
  },
  {
    "id": "TASK-006",
    "title": "Pages 写入与 PageNumber→PageId 映射",
    "description": "将 ParsedPage 转换为 DocumentPageModel 批量写入，构建 pageLookup 映射字典",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-04",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": ["TASK-001", "TASK-004"]
  },
  {
    "id": "TASK-007",
    "title": "Segments 写入与 SentenceId→SegmentId 映射",
    "description": "将 ParsedSegment 转换为 DocumentSegmentModel 批量写入，PageId 通过 pageLookup 获取，构建 segmentLookup 映射字典",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-05",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": ["TASK-006"]
  },
  {
    "id": "TASK-008",
    "title": "Questions 写入与 QuestionId→QuestionSegmentId 映射",
    "description": "将 ParsedQuestion 转换为 QuestionSegmentModel 批量写入，PageId 通过 pageLookup 获取，构建 questionLookup 映射字典",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-05",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": ["TASK-006"]
  },
  {
    "id": "TASK-009",
    "title": "Occurrences（Token）写入",
    "description": "将 segment 和 question 中的 Token 转换为 DocumentOccurrenceModel，SegmentId/QuestionSegmentId 通过映射字典获取",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-06",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": ["TASK-007", "TASK-008"]
  },
  {
    "id": "TASK-010",
    "title": "搜索索引同步（OpenSearch）",
    "description": "解析完成后调用 ISearchIndexService.IndexDocumentSegmentsAsync，失败仅记日志",
    "status": "done",
    "priority": "P1",
    "requirement": "REQ-PARSE-07",
    "files": ["src/Service/IngestionWorker.cs", "src/Service/OpenSearchIndexService.cs"],
    "depends_on": ["TASK-009"]
  },
  {
    "id": "TASK-012",
    "title": "错误处理与故障隔离",
    "description": "实现任务级 try/catch、FailIngestionJobAsync 嵌套保护、轮询级异常捕获",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-09",
    "files": ["src/Service/IngestionWorker.cs"],
    "depends_on": ["TASK-001", "TASK-002"]
  },
  {
    "id": "TASK-013",
    "title": "单元测试 — IngestionWorker 编排逻辑",
    "description": "测试轮询间隔、任务级异常隔离、映射正确性、索引失败不影响任务状态",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-01~09",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/IngestionWorkerTests.cs"],
    "depends_on": ["TASK-012"]
  },
  {
    "id": "TASK-014",
    "title": "单元测试 — DocumentDomainService 任务状态管理",
    "description": "测试 Start/Complete/Fail 状态转换、ErrorMessage 记录",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-02",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentDomainServiceTests.cs"],
    "depends_on": ["TASK-002"]
  },
  {
    "id": "TASK-015",
    "title": "单元测试 — DocumentParserService 解析逻辑",
    "description": "测试 PDF/Word/PPT 解析、OCR 后处理、句子边界、题目边界、Token 分词",
    "status": "done",
    "priority": "P0",
    "requirement": "REQ-PARSE-03",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/DocumentParserServiceTests.cs"],
    "depends_on": ["TASK-005"]
  },
  {
    "id": "TASK-016",
    "title": "集成测试 — 完整解析流程",
    "description": "端到端测试：创建文档 → Worker 处理 → 数据库验证 → 索引验证",
    "status": "pending",
    "priority": "P1",
    "requirement": "REQ-PARSE-01~09",
    "files": ["test/Ruoyu.Study.DocRetrieval.Tests/IngestionIntegrationTests.cs [当前无测试覆盖]"],
    "depends_on": ["TASK-013", "TASK-014", "TASK-015"]
  }
]
```

## 命令速查

### 构建与运行

```bash
# 构建解决方案
dotnet build Ruoyu.Study.DocRetrieval.sln

# 运行服务
dotnet run --project src/Host

# 运行所有测试
dotnet test

# 运行特定测试类
dotnet test --filter "FullyQualifiedName~IngestionWorkerTests"
dotnet test --filter "FullyQualifiedName~DocumentParserServiceTests"
```

### 数据库迁移

```bash
# 添加迁移
dotnet ef migrations add Init --project src/Database --startup-project src/Host

# 更新数据库
dotnet ef database update --project src/Database --startup-project src/Host
```

### Docker 依赖

```bash
# 启动 OpenSearch
docker run -d -p 9200:9200 -p 9600:9600 \
  -e "discovery.type=single-node" \
  -e "DISABLE_SECURITY_PLUGIN=true" \
  opensearchproject/opensearch:2.19.5
```

## 依赖图

```
TASK-001 (IngestionWorker 轮询)
├── TASK-006 (Pages 写入)
│   ├── TASK-007 (Segments 写入)
│   │   └── TASK-009 (Occurrences 写入)
│   │       └── TASK-010 (搜索索引同步)
│   └── TASK-008 (Questions 写入)
│       └── TASK-009 (Occurrences 写入)
│
TASK-002 (任务状态管理)
│
TASK-003 (解析器接口) ─── TASK-005 (解析器实现)
│
TASK-004 (领域模型) ─── TASK-005 (解析器实现)

TASK-001 + TASK-002 ─── TASK-012 (错误处理)

TASK-012 ─── TASK-013 (Worker 单元测试)
TASK-002 ─── TASK-014 (DomainService 单元测试)
TASK-005 ─── TASK-015 (Parser 单元测试)
TASK-013 + TASK-014 + TASK-015 ─── TASK-016 (集成测试)
```

### 关键路径

```
TASK-003 → TASK-004 → TASK-005 → TASK-006 → TASK-007 → TASK-008 → TASK-009 → TASK-010/011
```

### 可并行的任务

| 并行组 | 任务 |
|--------|------|
| 组 1 | TASK-001, TASK-002, TASK-003, TASK-004 |
| 组 2 | TASK-005, TASK-006（TASK-006 仅依赖 TASK-001 + TASK-004） |
| 组 3 | TASK-007, TASK-008（均依赖 TASK-006，可并行） |
| 组 4 | TASK-010（依赖 TASK-009） |
| 组 5 | TASK-013, TASK-014, TASK-015（测试任务可并行） |
