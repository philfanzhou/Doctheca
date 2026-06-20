# DocumentUpload — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：文档上传与导入任务创建是 Admin 端文档管理的核心入口，覆盖文件接收、格式校验、加密检测、哈希计算、OSS 上传、元数据校验、标题/哈希去重、文档记录创建与导入任务排队。本模块是文档检索服务的数据源头，其下依赖仓储接口与通用的 OSS 模块。

**用户故事（管理员上传文档）**：作为管理员，我要把一份教学文档（PDF/Word/PPT）上传到系统中，系统会自动校验文件格式与大小、检测是否加密、计算 SHA-256 哈希、上传到 OSS、校验元数据合法性、检查标题与文件哈希是否重复；若全部通过则创建一条状态为 `pending` 的文档记录和一条 `pending` 的导入任务，让我后续可以追踪导入进度。

## 功能要求清单（可独立测试）

- [x] REQ-UPLOAD-01 能接收 multipart/form-data 请求，提取 `file`、`title`、`subject`、`grade`、`year`、`tags` 字段。
- [x] REQ-UPLOAD-02 能校验文件存在性：`file` 为空或长度为 0 时返回 400（`DOCRETRIEVAL_FILE_REQUIRED`）。
- [x] REQ-UPLOAD-03 能校验文件大小：超过 200MB 返回 400。
- [x] REQ-UPLOAD-04 能校验文件格式：仅支持 PDF/Word/PPT 的 MIME 类型，不符返回 400（`DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED`）。
- [x] REQ-UPLOAD-05 能检测加密 PDF：在 PDF 文件前 4096 字节中搜索 `/Encrypt` 标记，命中返回 400（`DOCRETRIEVAL_FILE_ENCRYPTED`）。
- [x] REQ-UPLOAD-06 能计算文件 SHA-256 哈希，计算后将 `stream.Position` 重置为 0 再上传 OSS。
- [x] REQ-UPLOAD-07 能上传文件到 OSS，路径格式为 `docretrieval/{Guid}{ext}`，使用 `OssBucket.Uploads` 桶。
- [x] REQ-UPLOAD-08 能校验元数据：`title`/`subject`/`grade` 必填，`year` 可选；缺少必填项返回 400（`DOCRETRIEVAL_METADATA_REQUIRED`）；`title` 不超过 200 字符。
- [x] REQ-UPLOAD-08a 前端选择文件后，自动将文件名（不含扩展名）填入标题输入框；若标题字段已有内容则不覆盖。
- [x] REQ-UPLOAD-09 能校验学科与年级：学科仅支持"英语"（`DOCRETRIEVAL_SUBJECT_INVALID`），年级必须为 K/G1~G12（`DOCRETRIEVAL_GRADE_INVALID`）。
- [x] REQ-UPLOAD-10 能检测标题重复：相同标题已存在返回 409（`DOCRETRIEVAL_TITLE_ALREADY_EXISTS`）。
- [x] REQ-UPLOAD-11 能检测文件哈希重复：相同哈希且状态为 `ready` 的文档已存在返回 409（`DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS`）。
- [x] REQ-UPLOAD-12 能原子创建文档记录与导入任务：在同一 `SaveChangesAsync` 中写入 `documents` 和 `document_ingestion_jobs` 表。
- [x] REQ-UPLOAD-13 能根据 MIME 类型推导 `sourceType`：PDF→`pdf`、Word→`word`、PPT→`ppt`。
- [x] REQ-UPLOAD-14 能返回创建结果：包含 `documentId`、`title`、`jobId`、`status`。
- [x] REQ-UPLOAD-15 上传时从 JWT 提取用户 ID（`ClaimTypes.NameIdentifier`），写入 `documents.created_by` 字段。未登录或 claim 缺失时 `created_by` 为 null。

## 详细的验收标准（可自动验证）

### 场景 A：正常上传

- AC-A1：上传合法 PDF 文件，提供完整元数据（title="英语G3测试", subject="英语", grade="G3", year="2025"），返回 200，`success=true`，`data.documentId` 非 `Guid.Empty`，`data.status=="pending"`，`data.jobId` 非 `Guid.Empty`。
- AC-A2：数据库中 `documents` 表新增一条记录，`Status=="pending"`，`SourceType=="pdf"`，`FileHash` 为 64 位小写十六进制字符串，`FilePath` 以 `docretrieval/` 开头。
- AC-A3：数据库中 `document_ingestion_jobs` 表新增一条记录，`DocumentId` 与文档 `Id` 一致，`Status=="pending"`。
- AC-A4：`CreatedAt` 接近当前时间（误差 < 5s）。

### 场景 B：文件校验失败

- AC-B1：不传文件 → 400，`errorCode=="DOCRETRIEVAL_FILE_REQUIRED"`。
- AC-B2：文件大小 > 200MB → 400。
- AC-B3：文件 MIME 为 `image/png` → 400，`errorCode=="DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED"`。
- AC-B4：上传加密 PDF（含 `/Encrypt` 标记）→ 400，`errorCode=="DOCRETRIEVAL_FILE_ENCRYPTED"`。

### 场景 C：元数据校验失败

- AC-C1：缺少 `title` → 400，`errorCode=="DOCRETRIEVAL_METADATA_REQUIRED"`。
- AC-C2：`subject="math"` → 400，`errorCode=="DOCRETRIEVAL_SUBJECT_INVALID"`。
- AC-C3：`grade="college"` → 400，`errorCode=="DOCRETRIEVAL_GRADE_INVALID"`。
- AC-C4：`title` 超过 200 字符 → 400，`errorCode=="DOCRETRIEVAL_METADATA_REQUIRED"`。
- AC-C5：缺少 `year` → 允许上传，`year` 字段为空或 null。

### 场景 D：去重校验失败

- AC-D1：上传标题已存在的文档 → 409，`errorCode=="DOCRETRIEVAL_TITLE_ALREADY_EXISTS"`。
- AC-D2：上传文件哈希与状态为 `ready` 的文档相同 → 409，`errorCode=="DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS"`。
- AC-D3：文件哈希相同但状态非 `ready`（如 `pending`/`failed`）→ 允许上传，不触发 409。

## 非功能需求

- **性能**：文件哈希计算时间与文件大小线性相关，200MB 文件哈希计算应在 10s 内完成；OSS 上传时间取决于网络，整体请求超时 30s。
- **可靠性**：文档记录与导入任务在同一 `SaveChangesAsync` 中原子写入，避免部分写入；OSS 上传失败时数据库不写入（端点层先上传 OSS 再调用领域服务）。
- **安全**：不得在日志中记录文件完整哈希值或 OSS 访问签名；加密检测在文件流早期执行，避免后续无效计算。
- **可测试性**：`DocumentDomainService` 的所有外部依赖通过接口注入，可被 Moq 替换；`IsEncryptedPdf` 为静态方法，可独立测试。
- **幂等性**：标题唯一性约束和文件哈希去重保证相同内容不会重复入库。

## 测试策略

- **覆盖率目标**：`DocumentDomainService.CreateDocumentAsync` 代码行覆盖率 ≥ 80%，`DocumentAdminEndpoints.UploadDocument` 分支覆盖率 ≥ 75%。
- **必须测试的错误路径**：文件为空、文件格式不支持、加密 PDF、元数据缺失、学科非法、年级非法、标题重复、文件哈希重复（ready 状态）、文件哈希重复（非 ready 状态允许）。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（OSS、仓储、UnitOfWork、日志）均可替换。
- **禁止事项**：不得在测试中访问真实 OSS；不得写入真实生产数据库；不得依赖时间戳做精确相等断言（应使用时间范围）；不得在测试类中调用 `Task.Wait()`/`.Result`。
