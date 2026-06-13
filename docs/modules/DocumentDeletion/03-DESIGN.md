# DocumentDeletion — 架构与设计 (DESIGN)

## 1. 目录与文件结构

```
backend/ruoyu.docretrieval/
├── docs/modules/DocumentDeletion/
│   ├── 01-FEATURE.md
│   ├── 02-SPEC.md
│   ├── 03-DESIGN.md               ← 本文件
│   ├── 04-TASKS.md
│   ├── 05-TESTS.md
│   └── 06-CONVENTIONS.md
│
├── src/
│   ├── Domain/
│   │   ├── Repositories/
│   │   │   └── IRepositories.cs            # 各仓储接口定义
│   │   └── Services/
│   │       └── DocumentDomainService.cs     # DeleteDocumentAsync 核心逻辑
│   ├── Database/
│   │   └── Repositories/
│   │       ├── DocumentOccurrenceRepository.cs  # DeleteByDocumentIdAsync 实现
│   │       ├── QuestionSegmentRepository.cs     # DeleteByDocumentIdAsync 实现
│   │       ├── DocumentSegmentRepository.cs     # DeleteByDocumentIdAsync 实现
│   │       ├── DocumentPageRepository.cs        # DeleteByDocumentIdAsync 实现
│   │       ├── DocumentRepository.cs            # DeleteAsync, GetByTitleAsync 实现
│   │       └── UnitOfWork.cs                    # SaveChangesAsync 实现
│   └── Service/
│       └── DocumentAdminEndpoints.cs       # DELETE /admin/documents/{title} 端点
```

### 1.1 文件职责精确到每个文件

| 文件 | 职责 | 不得变更的公共 API |
|------|------|--------------------|
| `DocumentDomainService.cs` | 领域服务，编排级联删除 → SaveChanges → 索引清理流程 | `DeleteDocumentAsync(string title)` 签名 |
| `DocumentAdminEndpoints.cs` | Admin HTTP 端点，编排获取文档 → 删数据库 → 删 OSS 流程 | `DELETE /admin/documents/{title}` 路由 |
| `DocumentOccurrenceRepository.cs` | 实现 `DeleteByDocumentIdAsync`，删除指定文档的 occurrence 记录 | `DeleteByDocumentIdAsync` 签名 |
| `QuestionSegmentRepository.cs` | 实现 `DeleteByDocumentIdAsync`，删除指定文档的 question 记录 | `DeleteByDocumentIdAsync` 签名 |
| `DocumentSegmentRepository.cs` | 实现 `DeleteByDocumentIdAsync`，删除指定文档的 segment 记录 | `DeleteByDocumentIdAsync` 签名 |
| `DocumentPageRepository.cs` | 实现 `DeleteByDocumentIdAsync`，删除指定文档的 page 记录 | `DeleteByDocumentIdAsync` 签名 |
| `DocumentRepository.cs` | 实现 `GetByTitleAsync` 和 `DeleteAsync` | `GetByTitleAsync`, `DeleteAsync` 签名 |
| `UnitOfWork.cs` | 实现 `SaveChangesAsync`，统一事务提交 | `SaveChangesAsync` 签名 |

---

## 2. 关键接口签名与数据结构

### 2.1 Admin HTTP 端点

```
DELETE /admin/documents/{title}
```

**响应**：

```json
{
  "success": true,
  "data": {
    "title": "英语试卷2024",
    "deleted": true
  }
}
```

### 2.2 领域服务接口

```csharp
// src/Domain/Services/DocumentDomainService.cs
public class DocumentDomainService
{
    public async Task<bool> DeleteDocumentAsync(string title);
    public async Task<DocumentModel?> GetDocumentByTitleAsync(string title);
}
```

### 2.3 仓储接口

```csharp
// src/Domain/Repositories/IRepositories.cs
public interface IDocumentRepository
{
    Task<DocumentModel?> GetByTitleAsync(string title);
    Task<bool> DeleteAsync(Guid id);
}

public interface IDocumentOccurrenceRepository
{
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IQuestionSegmentRepository
{
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IDocumentSegmentRepository
{
    Task DeleteByDocumentIdAsync(Guid documentId);
}

public interface IDocumentPageRepository
{
    Task DeleteByDocumentIdAsync(Guid documentId);
}
```

### 2.4 外部服务接口

```csharp
// src/Domain/Repositories/ISearchIndexService.cs
public interface ISearchIndexService
{
    Task DeleteDocumentIndexAsync(Guid documentId);
}

// src/Domain/Repositories/IQdrantService.cs
public interface IQdrantService
{
    Task DeleteDocumentVectorsAsync(Guid documentId);
}

// Ruoyu.Study.Common/Oss/IOssService
public interface IOssService
{
    Task DeleteAsync(string objectPath);
}
```

### 2.5 依赖注入链

| 类型 | 来源 |
|------|------|
| `DocumentDomainService` | 构造函数注入 `IDocumentRepository`、`IDocumentPageRepository`、`IDocumentSegmentRepository`、`IQuestionSegmentRepository`、`IDocumentOccurrenceRepository`、`IDocumentIngestionJobRepository`、`IUnitOfWork`、`ILogger`、`ISearchIndexService?`、`IQdrantService?` |
| `DocumentAdminEndpoints` | 静态方法参数注入 `DocumentDomainService`、`IOssService`、`ILogger` |
| 各仓储接口 | 由 DI 提供 → 对应 Repository 实现 (Scoped) |
| `ISearchIndexService` | 由 DI 提供 → `OpenSearchIndexService` (可选) |
| `IQdrantService` | 由 DI 提供 → `QdrantService` (可选) |
| `IOssService` | 由 DI 提供 → `S3OssService` 或 `LocalFileOssService` |

---

## 3. 数据流描述

### 3.1 DeleteDocumentAsync（领域服务层）

```
[Caller: DocumentAdminEndpoints.DeleteDocument]
    │
    ▼
DocumentDomainService.DeleteDocumentAsync(title)
    │
    ├─ 1. 查询文档
    │     └─ _documentRepository.GetByTitleAsync(title)
    │     └─ document == null → return true（幂等）
    │
    ├─ 2. 级联删除关联数据
    │     ├─ _occurrenceRepository.DeleteByDocumentIdAsync(document.Id)
    │     ├─ _questionRepository.DeleteByDocumentIdAsync(document.Id)
    │     ├─ _segmentRepository.DeleteByDocumentIdAsync(document.Id)
    │     ├─ _pageRepository.DeleteByDocumentIdAsync(document.Id)
    │     └─ _documentRepository.DeleteAsync(document.Id)
    │
    ├─ 3. 提交数据库事务
    │     └─ _unitOfWork.SaveChangesAsync()
    │
    ├─ 4. 清理搜索索引（try/catch 容错）
    │     ├─ _searchIndexService?.DeleteDocumentIndexAsync(document.Id)
    │     └─ 异常被捕获 → _logger.LogError
    │
    ├─ 5. ~~清理向量索引（try/catch 容错）~~ — **已移除**（2026-06-12，Qdrant 已移除）
    │     ~~├─ _qdrantService?.DeleteDocumentVectorsAsync(document.Id)~~
    │     ~~└─ 异常被捕获 → _logger.LogError~~
    │
    └─ 6. 记录日志并返回
          ├─ _logger.LogInformation("文档已删除：{Title}", title)
          └─ return true
```

### 3.2 DeleteDocument（Admin 端点层）

```
[Caller: HTTP Client]
    │
    ▼
DocumentAdminEndpoints.DeleteDocument(title, documentService, ossService, logger)
    │
    ├─ 1. 获取文档信息（用于后续删 OSS）
    │     └─ documentService.GetDocumentByTitleAsync(title)
    │
    ├─ 2. 删除数据库记录 + 索引
    │     └─ documentService.DeleteDocumentAsync(title)
    │
    ├─ 3. 删除 OSS 文件（try/catch 容错）
    │     ├─ document != null && !string.IsNullOrEmpty(document.FilePath)
    │     │   └─ ossService.DeleteAsync(document.FilePath)
    │     └─ 异常被捕获 → logger.LogWarning
    │
    └─ 4. 返回结果
          └─ { success: true, data: { title, deleted: document != null } }
```

---

## 4. 错误处理策略

| 错误场景 | 处理方式 | 返回值 / 日志 |
|----------|----------|----------------|
| 文档不存在 | 直接返回 true | 无错误日志（幂等） |
| 搜索索引清理异常 | `try/catch` 捕获 | `LogError(ex, "删除文档搜索索引失败：{Title}", title)` |
| 向量索引清理异常 | `try/catch` 捕获 | `LogError(ex, "删除文档向量数据失败：{Title}", title)` |
| OSS 文件删除异常 | `try/catch` 捕获 | `LogWarning(ex, "删除文档文件失败：{FilePath}", document.FilePath)` |
| 数据库级联删除异常 | 冒泡给调用方 | 由上层处理 |
| `ISearchIndexService` 为 null | 跳过搜索索引清理 | 无日志 |
| `IQdrantService` 为 null | ~~跳过向量索引清理~~ — **已移除**（2026-06-12） | ~~无日志~~ |

---

## 5. 依赖的外部模块接口

| 接口 | 提供方 | 本模块用到的方法 |
|------|--------|------------------|
| `IDocumentRepository` | 本仓库 Domain | `GetByTitleAsync`, `DeleteAsync` |
| `IDocumentOccurrenceRepository` | 本仓库 Domain | `DeleteByDocumentIdAsync` |
| `IQuestionSegmentRepository` | 本仓库 Domain | `DeleteByDocumentIdAsync` |
| `IDocumentSegmentRepository` | 本仓库 Domain | `DeleteByDocumentIdAsync` |
| `IDocumentPageRepository` | 本仓库 Domain | `DeleteByDocumentIdAsync` |
| `IUnitOfWork` | 本仓库 Domain | `SaveChangesAsync` |
| `ISearchIndexService` | 本仓库 Domain | `DeleteDocumentIndexAsync` |
| `IQdrantService` | 本仓库 Domain | `DeleteDocumentVectorsAsync` |
| `IOssService` | `Ruoyu.Study.Common.Oss` 包 | `DeleteAsync` |

---

## 6. 可测试性设计

### 6.1 外部依赖通过接口注入

- 所有仓储、`ISearchIndexService`、`IQdrantService`、`IOssService` 全部为接口，可被 Moq 完全替代。
- `DocumentDomainService` 构造函数公开，`ISearchIndexService` 和 `IQdrantService` 为可选参数（nullable），测试中可传 null。

### 6.2 索引清理容错可验证

- 测试中可让 `DeleteDocumentIndexAsync` 抛出异常，验证方法仍返回 true。
- 测试中可让 ~~`DeleteDocumentVectorsAsync` 抛出异常，验证方法仍返回 true。~~ — **已移除**（2026-06-12，Qdrant 已移除）

### 6.3 OSS 清理容错可验证

- 测试中可让 `ossService.DeleteAsync` 抛出异常，验证端点仍返回成功。

### 6.4 禁止的反模式

- 禁止在 Admin 端点中直接访问 `DocRetrievalDbContext`；必须通过 `DocumentDomainService`。
- 禁止吞掉数据库级联删除异常；应冒泡给调用方。
- 禁止在 `DeleteDocumentAsync` 中直接调用 `IOssService`；OSS 清理由 Admin 端点负责。
