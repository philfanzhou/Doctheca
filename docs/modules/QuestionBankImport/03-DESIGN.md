# 03-DESIGN — QuestionBank 只读解析数据接口设计

## 组件

```text
QuestionBankImportEndpoints
  └─ QuestionBankService policy
      └─ IQuestionBankImportService
          ├─ DocLibraryDbContext (parses/files/blocks/images，只读)
          └─ IOssService (图片下载与 presigned URL)
```

`IQuestionBankImportService` 只保留：

```csharp
Task<(List<ImportableParseItem> Items, int TotalCount)> GetImportableListAsync(
    int page,
    int pageSize,
    string? search = null);

Task<(List<ParseBlockItem> Items, int TotalCount)> GetBlocksAsync(
    Guid parseId,
    int? pageId,
    string? blockType,
    int page,
    int pageSize);

Task<ParseImageBlob> GetImageBlobAsync(Guid imageId);
```

`ImportableParseItem` 不包含任何导入状态字段。

## 移除的运行时组件

- `POST .../import-status`
- `ImportStatusRequest` / `ImportStatusResult`
- `DocumentParseImportModel` / `ParseImportStatus`
- `IDocumentParseImportRepository` / `DocumentParseImportRepository`
- `DocumentParseImportEntity` / `DbSet<DocumentParseImportEntity>`
- `DatabaseInitializer` 中 `document_parse_imports` 建表 SQL
- `includeImported` 查询参数和 LEFT JOIN

升级不会自动执行 `DROP TABLE document_parse_imports`。已有遗留表保持不动，后续由单独、显式的数据清理变更处理。

## 路由

三个端点由独立 route group 映射到 `/internal/question-bank` 并统一应用 `QuestionBankService` 策略。它们不继承 `DocLibraryAdmin`，也不出现在 `/admin` 前缀下。
