# 文档元数据更新 — 功能规格（SPEC）

> 文档版本：v1.0
> 对应代码：`src/Domain/Services/DocumentDomainService.cs` → `UpdateMetadataAsync`
> 对应端点：`src/Service/DocumentAdminEndpoints.cs` → `UpdateMetadata`

---

## 1. 功能概述

`DocumentMetadata` 功能允许管理员修改已就绪（ready）文档的学科、年级、年份、标签等元数据。修改后，领域服务同步调用搜索索引服务更新索引中的元数据，保证搜索结果与数据库一致。

核心思想：**仅就绪可改、null 不改、索引同步、异常隔离。**

### 用户故事映射

- **US1：** 管理员希望修改文档元数据并同步搜索索引。→ 对应 FR-1 ~ FR-10。

---

## 2. 功能要求清单（Functional Requirements）

每一项均可通过单元测试独立验证。

- [ ] **FR-1 仅 ready 状态可修改** — 只有 `Status == "ready"` 的文档允许修改元数据。
- [ ] **FR-2 文档不存在抛异常** — 按标题查找文档，不存在时抛 `DocRetrievalValidationException("文档不存在")`。
- [ ] **FR-3 未就绪抛异常** — 文档存在但 `Status != "ready"` 时抛 `DocRetrievalValidationException("文档未就绪，不允许修改元数据")`。
- [ ] **FR-4 学科校验** — subject 非空时，仅支持"英语"（`DocRetrievalConstants.IsValidSubject`），否则抛 `DocRetrievalValidationException("学科仅支持：英语")`。
- [ ] **FR-5 年级校验** — grade 非空时，必须为 K/G1~G12（`DocRetrievalConstants.IsValidGrade`），否则抛 `DocRetrievalValidationException` 含有效值列表。
- [ ] **FR-6 null 参数不修改** — subject/grade/year/tags 为 null 时表示不修改，仅更新非 null 字段。
- [ ] **FR-7 更新时间戳** — 更新后设置 `UpdatedAt = DateTimeOffset.UtcNow`。
- [ ] **FR-8 搜索索引同步** — 更新成功后调用 `ISearchIndexService.UpdateDocumentMetadataAsync(documentId, subject, grade, year)`，失败仅记 Error 日志，不影响主流程。
- [ ] **FR-9 Admin 端点至少一项** — 请求体中 subject/grade/year/tags 全部为 null 时返回 400 "至少提供一项元数据"。
- [ ] **FR-10 tags 存储格式** — tags 字段从请求 JSON 中使用 `GetRawText()` 获取，存储为 JSON 数组字符串。

---

## 3. 详细验收标准

### 场景 1 — 成功更新学科

**Req:** FR-1, FR-4, FR-6, FR-7, FR-8
**Given:**
- 数据库中存在一条 `Title="TestDoc"`, `Status="ready"`, `Subject="英语"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then:**
- 文档的 `Subject` 仍为 "英语"
- `UpdatedAt` 被设置为当前时间
- `ISearchIndexService.UpdateDocumentMetadataAsync` 被调用一次
- 不抛异常

---

### 场景 2 — 文档不存在

**Req:** FR-2
**Given:**
- 数据库中不存在 `Title="NotFound"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("NotFound", subject: "英语", grade: null, year: null, tags: null)`

**Then:**
- 抛出 `DocRetrievalValidationException`，消息为 "文档不存在"

---

### 场景 3 — 文档未就绪

**Req:** FR-1, FR-3
**Given:**
- 数据库中存在一条 `Title="PendingDoc"`, `Status="pending"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("PendingDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then:**
- 抛出 `DocRetrievalValidationException`，消息为 "文档未就绪，不允许修改元数据"

---

### 场景 4 — 学科无效

**Req:** FR-4
**Given:**
- 数据库中存在一条 `Title="TestDoc"`, `Status="ready"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "数学", grade: null, year: null, tags: null)`

**Then:**
- 抛出 `DocRetrievalValidationException`，消息为 "学科仅支持：英语"

---

### 场景 5 — 年级无效

**Req:** FR-5
**Given:**
- 数据库中存在一条 `Title="TestDoc"`, `Status="ready"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: "G99", year: null, tags: null)`

**Then:**
- 抛出 `DocRetrievalValidationException`，消息包含 "年级取值非法"

---

### 场景 6 — null 参数不修改

**Req:** FR-6
**Given:**
- 数据库中存在一条 `Title="TestDoc"`, `Status="ready"`, `Subject="英语"`, `Grade="G5"`, `Year="2025"` 的文档

**When:**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: null, year: null, tags: null)`

**Then:**
- 文档的 `Subject`、`Grade`、`Year` 保持不变
- `UpdatedAt` 被更新

---

### 场景 7 — 搜索索引同步失败不中断主流程

**Req:** FR-8
**Given:**
- 数据库中存在一条 `Title="TestDoc"`, `Status="ready"` 的文档
- `ISearchIndexService.UpdateDocumentMetadataAsync` 抛出异常

**When:**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then:**
- 文档元数据已成功更新到数据库
- 不抛出异常
- 记录 Error 日志

---

### 场景 8 — Admin 端点无元数据返回 400

**Req:** FR-9
**Given:**
- 请求体为 `{}`

**When:**
- 调用 `PUT /admin/documents/TestDoc/metadata`，body 为 `{}`

**Then:**
- 返回 400，消息为 "至少提供一项元数据"

---

### 场景 9 — tags 使用 GetRawText 获取

**Req:** FR-10
**Given:**
- 请求体为 `{"tags": ["tag1","tag2"]}`

**When:**
- 调用 `PUT /admin/documents/TestDoc/metadata`

**Then:**
- tags 参数通过 `GetRawText()` 获取，值为 `["tag1","tag2"]` 字符串
- 存入数据库的 tags 字段为 `["tag1","tag2"]`

---

### 场景 10 — 错误码映射

**Req:** FR-2, FR-3, FR-4, FR-5
**Given:**
- 各种异常场景

**When/Then:**
| 异常消息 | HTTP 状态码 | errorCode |
|----------|-------------|-----------|
| 包含"不存在" | 404 | DOCRETRIEVAL_DOCUMENT_NOT_FOUND |
| 包含"未就绪" | 422 | DOCRETRIEVAL_DOCUMENT_NOT_READY |
| 包含"学科仅支持" | 400 | DOCRETRIEVAL_SUBJECT_INVALID |
| 包含"年级取值非法" | 400 | DOCRETRIEVAL_GRADE_INVALID |

---

## 4. 非功能需求（NFR）

- **NFR-1 性能**：单次元数据更新（含数据库写入 + 搜索索引同步）响应时间 ≤ 500 ms。
- **NFR-2 稳定性**：搜索索引服务不可用时，元数据更新仍能成功，仅日志记录失败。
- **NFR-3 可观测性**：元数据更新成功时记录 Information 日志，搜索索引同步失败时记录 Error 日志。
- **NFR-4 安全**：Admin 端点仅限管理员访问，不暴露内部异常堆栈。

---

## 5. 测试策略

| 层面 | 工具 | 覆盖范围 |
|------|------|----------|
| 单元测试 | xUnit + Moq | FR-1 ~ FR-10 |
| 集成测试 | WebApplicationFactory + InMemory DB | 端点错误码映射验证 |

测试命令：

```bash
dotnet test --filter "FullyQualifiedName~DocumentMetadataTests"
```
