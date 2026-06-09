# 文档列表查询与筛选 — 功能规格（SPEC）

> 文档版本：v1.0
> 对应代码：`src/Domain/Services/DocumentDomainService.cs` → `GetDocumentListAsync`
> 对应端点：`src/Service/DocumentAdminEndpoints.cs` → `ListDocuments`

---

## 1. 功能概述

`DocumentList` 功能提供文档列表的分页查询与多条件筛选能力。管理员可通过 Admin HTTP 端点按 status、subject、grade、keyword、year 等可选条件组合筛选文档，并以分页方式浏览结果。

核心思想：**全部筛选条件可选、分页参数自动修正、领域层与端点层职责分离。**

### 用户故事映射

- **US1：** 管理员希望按条件筛选文档并分页浏览。→ 对应 FR-1 ~ FR-5。

---

## 2. 功能要求清单（Functional Requirements）

每一项均可通过单元测试或集成测试独立验证。

- [ ] **FR-1 多条件筛选** — 支持按 status、subject、grade、keyword、year 五个条件筛选，全部可选；未提供的条件不参与过滤。
- [ ] **FR-2 分页参数修正** — `page ≤ 0` 修正为 1；`size ≤ 0` 修正为 20；`size > 100` 修正为 100。
- [ ] **FR-3 领域层返回结构** — `GetDocumentListAsync` 返回 `(List<DocumentModel> Items, int TotalCount)` 元组。
- [ ] **FR-4 Admin 端点返回结构** — `ListDocuments` 端点返回 JSON 包含 items、total、page、pageSize、totalPages 字段。
- [ ] **FR-5 items 中每条文档的字段映射** — 每条文档包含 id、title、source_type、subject、grade、year、tags（反序列化为数组或 null）、status、created_at、updated_at。

---

## 3. 详细验收标准

### 场景 1 — 无筛选条件返回全部文档

**Req:** FR-1, FR-3, FR-4
**Given:**
- 数据库中存在 25 条文档记录

**When:**
- 调用 `GET /admin/documents?page=1&pageSize=10`，不传任何筛选条件

**Then:**
- 返回 `items` 包含 10 条文档
- `total == 25`
- `page == 1`
- `pageSize == 10`
- `totalPages == 3`

---

### 场景 2 — 按 status 筛选

**Req:** FR-1
**Given:**
- 数据库中存在 5 条 `status=ready` 和 3 条 `status=pending` 的文档

**When:**
- 调用 `GET /admin/documents?status=ready`

**Then:**
- 返回 `total == 5`
- `items` 中每条文档的 `status == "ready"`

---

### 场景 3 — 按 subject 筛选

**Req:** FR-1
**Given:**
- 数据库中存在 4 条 `subject=英语` 的文档

**When:**
- 调用 `GET /admin/documents?subject=英语`

**Then:**
- 返回 `total == 4`
- `items` 中每条文档的 `subject == "英语"`

---

### 场景 4 — 按 grade 筛选

**Req:** FR-1
**Given:**
- 数据库中存在 3 条 `grade=G5` 的文档

**When:**
- 调用 `GET /admin/documents?grade=G5`

**Then:**
- 返回 `total == 3`
- `items` 中每条文档的 `grade == "G5"`

---

### 场景 5 — 按 year 筛选

**Req:** FR-1
**Given:**
- 数据库中存在 6 条 `year=2025` 的文档

**When:**
- 调用 `GET /admin/documents?year=2025`

**Then:**
- 返回 `total == 6`
- `items` 中每条文档的 `year == "2025"`

---

### 场景 6 — 按 keyword 筛选

**Req:** FR-1
**Given:**
- 数据库中存在标题包含"数学"的文档 2 条

**When:**
- 调用 `GET /admin/documents?keyword=数学`

**Then:**
- 返回 `total == 2`
- `items` 中每条文档的 `title` 包含"数学"

---

### 场景 7 — 多条件组合筛选

**Req:** FR-1
**Given:**
- 数据库中存在 `status=ready, subject=英语, grade=G5` 的文档 1 条

**When:**
- 调用 `GET /admin/documents?status=ready&subject=英语&grade=G5`

**Then:**
- 返回 `total == 1`
- `items` 中唯一文档同时满足三个条件

---

### 场景 8 — page≤0 修正为 1

**Req:** FR-2
**Given:**
- 数据库中存在 25 条文档

**When:**
- 调用 `GET /admin/documents?page=-1&pageSize=10`

**Then:**
- 返回 `page == 1`
- 返回第 1 页数据

---

### 场景 9 — size≤0 修正为 20

**Req:** FR-2
**Given:**
- 数据库中存在 25 条文档

**When:**
- 调用 `GET /admin/documents?page=1&pageSize=-5`

**Then:**
- 返回 `pageSize == 20`

---

### 场景 10 — size>100 修正为 100

**Req:** FR-2
**Given:**
- 数据库中存在 150 条文档

**When:**
- 调用 `GET /admin/documents?page=1&pageSize=200`

**Then:**
- 返回 `pageSize == 100`
- 返回最多 100 条数据

---

### 场景 11 — tags 字段反序列化

**Req:** FR-5
**Given:**
- 一条文档的 `tags` 存储为 `["tag1","tag2"]` JSON 字符串

**When:**
- 调用 `GET /admin/documents`

**Then:**
- 返回的 `tags` 字段为 JSON 数组 `["tag1","tag2"]`，而非字符串

---

### 场景 12 — tags 为 null 时返回 null

**Req:** FR-5
**Given:**
- 一条文档的 `tags` 字段为 null

**When:**
- 调用 `GET /admin/documents`

**Then:**
- 返回的 `tags` 字段为 null

---

## 4. 非功能需求（NFR）

- **NFR-1 性能**：在 10 000 条文档数据下，单次列表查询响应时间 ≤ 200 ms（含数据库查询）。
- **NFR-2 可观测性**：列表查询无需额外日志，但异常场景（数据库不可用等）应记录 Error 日志。
- **NFR-3 安全**：Admin 端点仅限管理员访问，不暴露内部异常堆栈。

---

## 5. 测试策略

| 层面 | 工具 | 覆盖范围 |
|------|------|----------|
| 单元测试 | xUnit + Moq | FR-1 ~ FR-5 |
| 集成测试 | WebApplicationFactory + InMemory DB | 端点返回结构验证 |

测试命令：

```bash
dotnet test --filter "FullyQualifiedName~DocumentListTests"
```
