# DocumentUpload — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "T01",
    "depends_on": [],
    "action": "评审 DocumentModels.cs 字段与数据模型：确认 DocumentModel 和 DocumentIngestionJobModel 的字段命名、默认值与 SPEC 一致；确认 Status 默认值为 'pending'，Language 默认值为 'en'。",
    "files": ["src/Domain/Models/DocumentModels.cs"],
    "acceptance": "代码阅读签名与文档一致；编译通过 dotnet build。",
    "notes": "DocumentModel.Id 默认 Guid.NewGuid()，CreatedAt 默认 DateTimeOffset.UtcNow。"
  },
  {
    "id": "T02",
    "depends_on": [],
    "action": "评审 Constants.cs 常量定义：确认 ValidSubjects 仅包含 '英语'，ValidGrades 包含 K/G1~G12 共 13 项，IsValidSubject/IsValidGrade 方法逻辑正确。",
    "files": ["src/Domain/Models/Constants.cs"],
    "acceptance": "dotnet build；ValidSubjects.Length==1，ValidGrades.Length==13。",
    "notes": "GradeLabels 字典为辅助映射，不影响校验逻辑。"
  },
  {
    "id": "T03",
    "depends_on": [],
    "action": "评审 IRepositories.cs 仓储接口：确认 IDocumentRepository 包含 GetByTitleAsync、GetByFileHashAndStatusAsync、AddAsync 方法；确认 IDocumentIngestionJobRepository 包含 AddAsync 方法；确认 IUnitOfWork 包含 SaveChangesAsync。",
    "files": ["src/Domain/Repositories/IRepositories.cs"],
    "acceptance": "dotnet build；接口与 DocumentRepository 实现一一对应。",
    "notes": "GetByFileHashAndStatusAsync 是哈希去重的关键方法，必须接受 status 参数。"
  },
  {
    "id": "T04",
    "depends_on": ["T01", "T02", "T03"],
    "action": "评审 DocumentDomainService.CreateDocumentAsync 核心逻辑：确认调用顺序为 ValidateDocumentMetadata → GetByTitleAsync → GetByFileHashAndStatusAsync → AddAsync(document) → AddAsync(job) → SaveChangesAsync；确认 ValidateDocumentMetadata 校验 Title/Subject/Grade/Year/FileHash。",
    "files": ["src/Domain/Services/DocumentDomainService.cs"],
    "acceptance": "代码走查流程与 DESIGN 数据流一致；编译通过。",
    "notes": "GetByFileHashAndStatusAsync 第二参数必须为 'ready'，非 ready 状态允许重复哈希。"
  },
  {
    "id": "T05",
    "depends_on": ["T04"],
    "action": "评审 DocumentAdminEndpoints.UploadDocument 端点逻辑：确认校验顺序为 FormContentType → File存在性 → File大小 → MIME类型 → 元数据必填 → 加密检测 → SHA256计算 → stream.Position=0 → OSS上传 → sourceType推导 → CreateDocumentAsync → 异常映射。",
    "files": ["src/Service/DocumentAdminEndpoints.cs"],
    "acceptance": "代码走查流程与 DESIGN 数据流一致；编译通过。",
    "notes": "IsEncryptedPdf 必须在 SHA256 计算之前执行，避免对加密文件做无效计算。"
  },
  {
    "id": "T06",
    "depends_on": ["T05"],
    "action": "评审 IsEncryptedPdf 加密检测逻辑：确认仅对 PDF 类型检测；读取前 4096 字符搜索 '/Encrypt'；确认 stream.Position 在 finally 块中恢复。",
    "files": ["src/Service/DocumentAdminEndpoints.cs"],
    "acceptance": "代码走查逻辑正确；非 PDF 返回 false；PDF 含 /Encrypt 返回 true。",
    "notes": "使用 StreamReader 读取，leaveOpen=true 保证流不被关闭。"
  },
  {
    "id": "T07",
    "depends_on": ["T04", "T05"],
    "action": "评审错误码映射逻辑：确认 DocRetrievalValidationException 的消息文本与 HTTP 状态码/错误码的映射关系与 SPEC 一致（文档名已存在→409/TITLE_ALREADY_EXISTS，文件已被导入→409/HASH_ALREADY_EXISTS，学科仅支持→400/SUBJECT_INVALID，年级取值非法→400/GRADE_INVALID）。",
    "files": ["src/Service/DocumentAdminEndpoints.cs"],
    "acceptance": "代码走查映射关系与 SPEC 错误码表一致。",
    "notes": "映射基于消息文本 Contains 匹配，需确保领域服务消息文本稳定。"
  },
  {
    "id": "T08",
    "depends_on": ["T01", "T02", "T03", "T04", "T05", "T06", "T07"],
    "action": "执行完整测试套件：编译解决方案并运行所有 DocumentUpload 相关测试，验证覆盖率摘要。",
    "files": ["src/**/*.cs", "test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~DocumentUpload；零警告，无测试失败。",
    "notes": "覆盖率目标：CreateDocumentAsync ≥ 80%，UploadDocument 分支覆盖率 ≥ 75%。"
  }
]
```

## 命令速查

```bash
# 编译
dotnet build src/services/ruoyu.docretrieval/Ruoyu.Study.DocRetrieval.sln

# 运行所有测试
dotnet test src/services/ruoyu.docretrieval/Ruoyu.Study.DocRetrieval.sln --configuration Release

# 运行特定测试
dotnet test --filter FullyQualifiedName~DocumentDomainServiceTests
dotnet test --filter FullyQualifiedName~DocumentAdminEndpointsTests
```

## 任务依赖图

```
T01 ──┐
T02 ──┤
T03 ──┼──▶ T04 ──┐
       │          ├──▶ T07 ──┐
       │    T05 ──┘          │
       │      │              │
       │      ├──▶ T06 ──┐  │
       │      │          ├──▶ T08
       │      └──────────┘  │
       └────────────────────┘
```
