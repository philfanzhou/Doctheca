# 文档列表查询与筛选 — 命名与代码规范（CONVENTIONS）

> 适用于：`DocumentDomainService.cs`（列表相关方法）、`DocumentAdminEndpoints.cs`（ListDocuments 端点），以及未来扩展到该功能的所有相关文件。

---

## 1. 命名约定

### 1.1 文件与命名空间

- **命名空间**：
  - 领域服务放在 `Ruoyu.Study.DocRetrieval.Domain.Services` 命名空间下；
  - Admin 端点放在 `Ruoyu.Study.DocRetrieval.Service` 命名空间下。

### 1.2 类与方法

| 元素 | 约定 | 示例 |
|------|------|------|
| 领域服务方法 | 动宾短语 + `Async` 后缀 | `GetDocumentListAsync` |
| Repository 方法 | 动宾短语 + `Async` 后缀 | `GetListAsync` |
| Admin 端点方法 | PascalCase，描述动作 | `ListDocuments` |
| 返回元组字段 | PascalCase | `Items`、`TotalCount` |

### 1.3 字段与局部变量

- **私有字段**：`_` 前缀 + `camelCase`。示例：`_documentRepository`、`_logger`。
- **局部变量**：`camelCase`。示例：`items`、`totalCount`、`page`、`size`。
- **查询参数**：与 HTTP query string 参数名保持一致，使用 `camelCase`。示例：`pageSize`、`status`、`subject`。

### 1.4 HTTP 端点命名

- 路由：`GET /admin/documents`
- 查询参数：`page`、`pageSize`、`status`、`subject`、`grade`、`keyword`、`year`
- 响应字段：`snake_case`（`source_type`、`created_at`、`updated_at`），与项目现有风格一致

---

## 2. 日志与安全要求

### 2.1 日志消息模板

- 列表查询为纯读操作，正常路径无需额外日志。
- 异常路径由 ASP.NET Core 中间件或全局异常处理器统一记录。

### 2.2 敏感信息处理

- 列表查询不涉及敏感信息输出，文档标题、学科、年级等均为非敏感数据。
- 不在日志中记录查询参数中的用户输入（防止日志注入）。

---

## 3. 错误消息格式约定

- 列表查询不抛出 `DocRetrievalValidationException`，分页参数修正为静默修正，不返回错误。
- 数据库异常由上层中间件统一处理，返回 500 + 通用错误消息。

---

## 4. 测试工具要求

- **测试框架**：xUnit（`[Fact]`）。
- **Mock 库**：Moq（`Mock<IDocumentRepository>`）。
- **测试方法命名**：`{被测方法}_{场景}_Should{预期结果}`，例如：
  - `GetDocumentList_NoFilter_ReturnsAllDocuments`
  - `GetDocumentList_PageZero_CorrectedToOne`
  - `GetDocumentList_SizeOver100_CorrectedTo100`

---

## 5. 代码风格

- **缩进**：4 空格，不混用 tab。
- **`async/await`**：所有异步方法使用 `await`。
- **参数修正**：使用 `if` 语句直接修正，不使用 `Math.Max` / `Math.Min`（保持与现有代码风格一致）。
- **LINQ 映射**：端点层使用 `items.Select(d => new { ... })` 匿名对象映射，不引入额外的 DTO 类。
- **JSON 序列化**：tags 字段使用 `JsonSerializer.Deserialize<string[]>(d.Tags)` 反序列化，null 时返回 null。
- **分页计算**：`totalPages = (totalCount + pageSize - 1) / pageSize`，整数除法向上取整。

---

## 6. 审批清单（Review Checklist）

提交/合入本功能相关代码前，至少确认：

- [ ] 方法命名遵循"动宾短语 + `Async`"约定
- [ ] 分页参数修正逻辑在领域层完成（page/size 修正）
- [ ] Admin 端点返回 JSON 包含 items/total/page/pageSize/totalPages
- [ ] tags 字段正确反序列化为数组或 null
- [ ] 所有筛选条件均为可选参数
- [ ] `dotnet build -c Release` 无错误
- [ ] 相关单元测试全部通过
