# ExactSearch — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.DocLibrary.Domain.*`；服务层使用 `Ruoyu.Study.DocLibrary.Service.*`；数据库层使用 `Ruoyu.Study.DocLibrary.Database.*`。
- **类名**：领域服务采用 `XxxDomainService`；模型采用 `XxxModel`；仓储接口采用 `IXxxRepository`/`IXxxService`；仓储实现采用 `XxxRepository`/`XxxService`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `ExactSearchAsync`、`IndexParseBlocksAsync`。
- **返回值类**：HTTP 端点层返回 JSON 响应；领域层返回元组 `(List<SearchResultModel>, int, string?)`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_searchIndexService`）。
- **数据库**：表名小写蛇形；列名小写蛇形（如 `document_file_id`、`page_number`、`block_id`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的查询词内容（可记录长度或哈希）；不得记录用户身份信息。
- **日志级别**：
  - `Information`：关键操作，如 OpenSearch 查询成功。
  - `Warning`：非致命错误，如 OpenSearch 查询失败返回空结果。必须包含异常信息以便排查。
  - `Error`：严重错误，如数据库查询失败。
- **错误消息**：对外返回的 HTTP API 错误消息不应包含内部异常堆栈或数据库细节，应提供面向用户的简短描述并附带错误码。

## 错误消息格式约定

| 场景 | 错误码 | 消息文本 |
| --- | --- | --- |
| 查询词为空 | `DOCLIBRARY_QUERY_REQUIRED` | `"DOCLIBRARY_QUERY_REQUIRED: 查询词不能为空"` |
| 查询词超过 200 字符 | `DOCLIBRARY_QUERY_TOO_LONG` | `"DOCLIBRARY_QUERY_TOO_LONG: 查询词超过200字符"` |

> 注：错误码与消息文本之间使用冒号+空格分隔；HTTP 端点层返回 400 Bad Request，响应体为 `{ success: false, message: "...", errorCode: "..." }`。pageSize 超过 100 时不再报错，静默截断为 100。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（ISearchIndexService）必须通过 Mock 替换，不得在测试中访问真实 OpenSearch 或数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null/Empty`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `ExactSearchAsync_Unavailable_ReturnsEmpty`。
- **确定性**：分页结果只断言数量和排序方向；不依赖时间戳做精确相等断言。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 公共方法在必要时应提供参数 guard（例如 `pageSize` 限制在 1-100，默认 20）。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- `ISearchIndexService` 通过 `IServiceProvider.GetService` 可选获取，不强制注册。
- 搜索结果默认按 `Score` 降序排列；分页通过 `search_after` 游标实现。
