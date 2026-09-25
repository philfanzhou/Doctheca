# ruoyu.doclibrary 后端代码规范审计报告

> 历史快照（2026-07-22）：文中 `QuestionBankImport*` 是 DocLibrary 自己的内部集成代码，其后已随 AdminAuthentication 重构移除；其命名所指的原 QuestionBank 服务亦已迁出为外部仓库 Quaestura（ADR-0011）。为保审计记录准确，保留原文件名不改。

- 审计日期：2026-07-22
- 扫描范围：`src/services/ruoyu.doclibrary/src/**/*.cs`（排除 obj/、bin/）
- 扫描文件数：72

## 总结

- 违规总数：**197**
- 按类型分布：
  - `async_method_naming`: 96
  - `chinese_comment_or_message`: 46
  - `null_forgiving_operator`: 46
  - `multiple_public_types`: 9

## 大文件清单（≥500 行）

| 文件 | 行数 | 备注 |
|------|------|------|
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 791 | 建议拆分或提取私有方法 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 745 | 建议拆分或提取私有方法 |
| `src/services/ruoyu.doclibrary/src/Service/MinerUFileParseWorker.cs` | 698 | 建议拆分或提取私有方法 |
| `src/services/ruoyu.doclibrary/src/Service/OpenSearchIndexService.cs` | 668 | 建议拆分或提取私有方法 |

## 优先级建议

- **P0**：`async_void`、导致编译/运行时问题的严重结构违规。
- **P1**：`namespace_block_scoped`、`multiple_public_types`、`endpoint_not_static`、`endpoint_name_suffix`、`worker_not_backgroundservice`、`options_class_naming`、私有字段命名、异步方法命名。
- **P2**：`chinese_*`（注释/日志/异常消息中文化）、`null_forgiving_operator` 滥用、大文件拆分。

## 详细违规清单

| 文件 | 行号 | 问题类型 | 问题描述 | 建议修复 |
|------|------|----------|----------|----------|
| `src/services/ruoyu.doclibrary/src/Database/DatabaseInitializer.cs` | 25 | `chinese_comment_or_message` | // document_parses: 新增 content_list + zip_path（阶段 2） | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Database/DatabaseInitializer.cs` | 28 | `chinese_comment_or_message` | // document_parses: model_version (解析模型版本 vlm/pipeline) | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Database/DocLibraryDbContext.cs` | 12 | `null_forgiving_operator` | count=1: public DbSet<DocumentFileEntity> DocumentFiles { get; set; } = null!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Database/DocLibraryDbContext.cs` | 13 | `null_forgiving_operator` | count=1: public DbSet<DocumentParseEntity> DocumentParses { get; set; } = null!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Database/DocLibraryDbContext.cs` | 14 | `null_forgiving_operator` | count=1: public DbSet<DocumentParseImageEntity> DocumentParseImages { get; set; } = null!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Database/DocLibraryDbContext.cs` | 15 | `null_forgiving_operator` | count=1: public DbSet<DocumentParseBlockEntity> DocumentParseBlocks { get; set; } = null!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Database/DocLibraryDbContext.cs` | 16 | `null_forgiving_operator` | count=1: public DbSet<DocumentParseImportEntity> DocumentParseImports { get; set; } = null!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Database/Repositories/DocumentParseImageRepository.cs` | 37 | `null_forgiving_operator` | count=1: .Where(e => e.Parse!.DocumentFileId == documentFileId) | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Domain/Models/DocumentParseImportModel.cs` | 21 | `multiple_public_types` | multiple public types: class DocumentParseImportModel, class ParseImportStatus | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Domain/Services/DocumentParseBlockService.cs` | 21 | `multiple_public_types` | multiple public types: interface IDocumentParseBlockService, class DocumentParseBlockService | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Domain/Services/IQuestionBankImportService.cs` | 70 | `multiple_public_types` | multiple public types: interface IQuestionBankImportService, record ImportableParseItem, record ParseBlockItem, record ParseImageBlob, record ImportStatusResult | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 107 | `chinese_comment_or_message` | // FileConversion: 命名 HttpClient + Singleton 包装。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 108 | `chinese_comment_or_message` | // AddHttpClient<TInterface, TImplementation>() 默认注册为 Transient，MinerUFileParseWorker 每 5 秒 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 109 | `chinese_comment_or_message` | // CreateScope().GetRequiredService<IFileConversionService>() 会重复 new 实例（构造函数日志被重复打印）。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 110 | `chinese_comment_or_message` | // 改用 AddHttpClient("FileConversion") 注册命名 HttpClient（HttpClientFactory 池化 Handler）， | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 111 | `chinese_comment_or_message` | // 再用 AddSingleton<IFileConversionService> 工厂方式包装，确保真正的 Singleton 生命周期。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 132 | `chinese_comment_or_message` | // Note: DocLibrary 是内网管理后台，无应用层认证。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 133 | `chinese_comment_or_message` | // 所有 /admin/* 端点 AllowAnonymous，访问控制由部署层网络隔离实现。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Host/Program.cs` | 134 | `chinese_comment_or_message` | // 详见 docs/overview/Design.md "访问控制架构" 章节。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 31 | `async_method_naming` | ExportMarkdown | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 51 | `async_method_naming` | ExportHtml | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 71 | `async_method_naming` | ExportParseMarkdown | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 93 | `async_method_naming` | ExportParseHtml | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 117 | `async_method_naming` | ExportMarkdownCore | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentExportEndpoints.cs` | 135 | `async_method_naming` | ExportHtmlCore | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 46 | `async_method_naming` | UploadDocumentFile | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 76 | `chinese_comment_or_message` | // DocLibrary 是内网管理后台，无应用层认证（2026-07-04 移除 JWT）。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 77 | `chinese_comment_or_message` | // documents.created_by 字段保留为 null；后续接入审计场景时再恢复写入。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 102 | `async_method_naming` | ListDocumentFiles | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 163 | `async_method_naming` | GetDocumentFile | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 235 | `async_method_naming` | ParseDocumentFile | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 271 | `async_method_naming` | UpdateDocumentFileMetadata | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 327 | `async_method_naming` | DeleteDocumentFile | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentFileEndpoints.cs` | 409 | `multiple_public_types` | multiple public types: class DocumentFileEndpoints, record UpdateMetadataRequest | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentParseEndpoints.cs` | 26 | `async_method_naming` | ListDocumentParses | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentParseEndpoints.cs` | 62 | `async_method_naming` | DeleteDocumentParse | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/DocumentSearchEndpoints.cs` | 24 | `async_method_naming` | Search | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/QuestionBankImportEndpoints.cs` | 31 | `async_method_naming` | ListImportableParses | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/QuestionBankImportEndpoints.cs` | 62 | `async_method_naming` | GetParseBlocks | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/QuestionBankImportEndpoints.cs` | 114 | `async_method_naming` | GetImage | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/QuestionBankImportEndpoints.cs` | 140 | `async_method_naming` | UpsertImportStatus | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Service/Endpoints/QuestionBankImportEndpoints.cs` | 199 | `multiple_public_types` | multiple public types: class QuestionBankImportEndpoints, record ImportStatusRequest | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Service/MinerUFileParseWorker.cs` | 303 | `chinese_comment_or_message` | // 传入文件已有的 subject/grade/year，确保索引时元数据可被过滤 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/MinerUFileParseWorker.cs` | 312 | `chinese_comment_or_message` | /// 传入文件已有的 subject/grade/year，索引时即可被过滤；后续 LLM 分析或手动更新会通过 UpdateDocumentFileMetadataAsync 同步。 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/MinerUPrecisionClient.cs` | 110 | `null_forgiving_operator` | count=1: ? taskIdEl.GetString()! | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Service/MinerUPrecisionClient.cs` | 309 | `multiple_public_types` | multiple public types: class MinerUPrecisionClient, record MinerUParseResult, class MinerUOptions | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Service/OpenSearchIndexService.cs` | 452 | `chinese_comment_or_message` | // file_name 是 keyword 类型，使用 term 精确匹配 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Service/PdfSplitService.cs` | 25 | `multiple_public_types` | multiple public types: interface IPdfSplitService, class PdfSplitService | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Service/QuestionBankImportService.cs` | 140 | `null_forgiving_operator` | count=1: var url = await _ossService.GetPresignedUrlAsync(b.Image!.ImagePath, PresignedUrlExpirySeconds); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Service/QuestionBankImportService.cs` | 146 | `null_forgiving_operator` | count=1: b.Image!.Id, b.Image.ImagePath); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Service/RemoteFileConversionService.cs` | 26 | `multiple_public_types` | multiple public types: interface IFileConversionService, class FileConversionOptions, class RemoteFileConversionService | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/ConstantsTests.cs` | 54 | `chinese_comment_or_message` | [InlineData("高一", false)] // Labels are for display, not validation | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 80 | `null_forgiving_operator` | count=1: var markdown = "&#33;&#91;a](documents/mineru/task-1/a.jpg)\n\n&#33;&#91;b](documents/mineru/task-1/b.png)"; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 95 | `null_forgiving_operator` | count=1: result.Should().Be("&#33;&#91;a](images/a.jpg)\n\n&#33;&#91;b](images/b.png)"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 192 | `async_method_naming` | ZipExport_ContainsMarkdownAndImages | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 195 | `null_forgiving_operator` | count=1: var markdownContent = "# Test\n\n&#33;&#91;img](images/test.jpg)"; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 223 | `null_forgiving_operator` | count=1: var readMdEntry = readArchive.GetEntry("test.md")!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentExportLogicTests.cs` | 228 | `null_forgiving_operator` | count=1: var readImgEntry = readArchive.GetEntry($"images/{imageName}")!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileDeleteCleanupTests.cs` | 38 | `async_method_naming` | Cleanup_CollectsAllOssPaths_BeforeDeletion | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileDeleteCleanupTests.cs` | 100 | `async_method_naming` | Cleanup_HandlesNullZipPath | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileDeleteCleanupTests.cs` | 130 | `async_method_naming` | Cleanup_DedupesDuplicatePaths | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileDeleteCleanupTests.cs` | 142 | `null_forgiving_operator` | count=1: new() { Id = Guid.NewGuid(), DocumentFileId = fileId, Status = "parsed", ZipPath = "docs/mineru.zip" }, // same zip! | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 25 | `async_method_naming` | CreateAsync_SetsCreatedAtAndReturnsModel | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 49 | `async_method_naming` | GetByIdAsync_ReturnsModel_WhenExists | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 61 | `null_forgiving_operator` | count=1: result!.Id.Should().Be(id); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 65 | `async_method_naming` | GetByIdAsync_ReturnsNull_WhenNotExists | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 79 | `async_method_naming` | GetListAsync_ReturnsPagedResults | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 99 | `async_method_naming` | DeleteAsync_DelegatesToRepository | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 114 | `multiple_public_types` | multiple public types: class DocumentFileServiceTests, class DocumentParseServiceTests | 将额外的 public 类型拆分到独立文件 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 130 | `async_method_naming` | CreateAsync_SetsStatusToPendingAndReturnsModel | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 150 | `async_method_naming` | CreateAsync_SetsModelVersion_ToVlm_ByDefault | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 166 | `async_method_naming` | CreateAsync_SetsModelVersion_ToPipeline_WhenSpecified | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 182 | `async_method_naming` | GetLatestByFileIdAndModelAsync_ReturnsParseForSpecificModel | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 194 | `null_forgiving_operator` | count=1: result!.ModelVersion.Should().Be("pipeline"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 198 | `async_method_naming` | GetByIdAsync_ReturnsModel_WhenExists | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 210 | `null_forgiving_operator` | count=1: result!.Id.Should().Be(id); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 214 | `async_method_naming` | GetLatestByFileIdAsync_ReturnsLatestParse | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 226 | `null_forgiving_operator` | count=1: result!.DocumentFileId.Should().Be(fileId); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 230 | `async_method_naming` | UpdateStatusAsync_UpdatesStatusAndFields | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 249 | `async_method_naming` | UpdateStatusAsync_SetsParsedAt_WhenStatusIsParsed | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 266 | `async_method_naming` | UpdateStatusAsync_ThrowsKeyNotFound_WhenNotExists | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 280 | `async_method_naming` | UpdateStatusAsync_SetsErrorMessage_WhenProvided | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 296 | `async_method_naming` | UpdateStatusAsync_SetsLayoutJson_WhenProvided | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 315 | `async_method_naming` | UpdateStatusAsync_DoesNotOverwriteLayoutJson_WhenNull | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 336 | `async_method_naming` | GetPendingJobsAsync_ReturnsOnlyPendingJobs | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 355 | `async_method_naming` | AddImageAsync_CallsRepository | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 374 | `async_method_naming` | GetImagesByParseIdAsync_ReturnsImages | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 393 | `async_method_naming` | GetImagesByFileIdAsync_ReturnsImages | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 411 | `async_method_naming` | GetListAsync_ReturnsPagedResults_WithSearch | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 432 | `async_method_naming` | GetListAsync_ReturnsPagedResults_WithoutSearch | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 451 | `async_method_naming` | DeleteParseAsync_DeletesImagesAndParse | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentFileServiceTests.cs` | 470 | `async_method_naming` | DeleteParseAsync_ReturnsFalse_WhenNotFound | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 28 | `async_method_naming` | InsertBlocksFromContentListAsync_WithValidJson_InsertsAllBlocks | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 51 | `null_forgiving_operator` | count=1: captured!.Should().HaveCount(3); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 62 | `async_method_naming` | InsertBlocksFromContentListAsync_AssignsSortIndexPerPage | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 83 | `null_forgiving_operator` | count=1: var page0 = captured!.Where(b => b.PageId == 0).ToList(); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 94 | `async_method_naming` | InsertBlocksFromContentListAsync_WithoutImageMap_LeavesImageIdNull | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 112 | `null_forgiving_operator` | count=1: captured![0].ImageId.Should().BeNull(); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 116 | `async_method_naming` | InsertBlocksFromContentListAsync_PreservesRawBlockData | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 133 | `null_forgiving_operator` | count=1: captured![0].BlockData.Should().Contain("extra_field"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 138 | `async_method_naming` | InsertBlocksFromContentListAsync_WithEmptyJson_DoesNotCallRepo | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 149 | `async_method_naming` | InsertBlocksFromContentListAsync_WithEmptyString_DoesNotCallRepo | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 160 | `async_method_naming` | InsertBlocksFromContentListAsync_WithInvalidJson_DoesNotCallRepo | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 171 | `async_method_naming` | InsertBlocksFromContentListAsync_DefaultsPageIdToZero_WhenMissing | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 188 | `null_forgiving_operator` | count=1: captured![0].PageId.Should().Be(0); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 192 | `async_method_naming` | InsertBlocksFromContentListAsync_ExtractsTextFromContentOrBodyField | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 209 | `null_forgiving_operator` | count=1: captured![0].TextContent.Should().Contain("<table>"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 215 | `async_method_naming` | InsertBlocksFromContentListAsync_ExtractsMinerUFields_PipelineBbox | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 242 | `null_forgiving_operator` | count=1: captured![0].SubType.Should().Be("text"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 255 | `async_method_naming` | InsertBlocksFromContentListAsync_NormalizesVlmBboxTo1000 | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 279 | `null_forgiving_operator` | count=1: captured![0].BboxX0.Should().Be(100f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 293 | `async_method_naming` | InsertBlocksFromContentListAsync_NoMinerUFields_DefaultsApplied | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/DocumentParseBlockServiceTests.cs` | 311 | `null_forgiving_operator` | count=1: captured![0].SubType.Should().BeNull(); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 33 | `async_method_naming` | ConvertToPdfAsync_ReturnsStream_OnSuccess | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 47 | `null_forgiving_operator` | count=1: result!.Length.Should().Be(fakePdf.Length); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 51 | `async_method_naming` | ConvertToPdfAsync_ReturnsNull_OnNonSuccessStatusCode | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 67 | `async_method_naming` | ConvertToPdfAsync_ReturnsNull_OnHttpRequestException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 80 | `async_method_naming` | ConvertToPdfAsync_SeeksStreamToStartBeforeReading | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 85 | `null_forgiving_operator` | count=1: capturedBody = await req.Content!.ReadAsByteArrayAsync(); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/FileConversionServiceTests.cs` | 126 | `async_method_naming` | ConvertToPdfAsync_SendsMultipartFormData | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/MinerUFileParseWorkerTests.cs` | 91 | `null_forgiving_operator` | count=1: CreateChunkResult(null!), | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 118 | `null_forgiving_operator` | count=1: "test", phrase: false, filter: null, pageSize: 10, pageToken: "!!!invalid base64!!!"); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 311 | `null_forgiving_operator` | count=1: var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(nextToken!)); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 565 | `chinese_comment_or_message` | Subject = "英语",  // V1 filter only | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 710 | `null_forgiving_operator` | count=1: results[0].Bbox!.Should().HaveCount(4); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 711 | `null_forgiving_operator` | count=1: results[0].Bbox![0].Should().Be(100.5f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 712 | `null_forgiving_operator` | count=1: results[0].Bbox![1].Should().Be(200.0f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 713 | `null_forgiving_operator` | count=1: results[0].Bbox![2].Should().Be(300.25f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 714 | `null_forgiving_operator` | count=1: results[0].Bbox![3].Should().Be(400.0f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 785 | `null_forgiving_operator` | count=1: results[0].Bbox!.Should().HaveCount(4); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 786 | `null_forgiving_operator` | count=1: results[0].Bbox![0].Should().Be(50.0f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 787 | `null_forgiving_operator` | count=1: results[0].Bbox![1].Should().Be(0f);  // y0 missing → 0 | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 788 | `null_forgiving_operator` | count=1: results[0].Bbox![2].Should().Be(0f);  // x1 missing → 0 | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/OpenSearchIndexServiceTests.cs` | 789 | `null_forgiving_operator` | count=1: results[0].Bbox![3].Should().Be(600.0f); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 132 | `async_method_naming` | GetImportableList_OnlyReturnsParsedStatus | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 134 | `chinese_comment_or_message` | // UT-QBI-01: 仅返回 status=parsed 的 parse | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 150 | `async_method_naming` | GetImportableList_SearchMatchesFileName | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 152 | `chinese_comment_or_message` | // UT-QBI-02: search 模糊匹配 file_name | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 166 | `async_method_naming` | GetImportableList_ExcludesImportedByDefault | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 168 | `chinese_comment_or_message` | // UT-QBI-03: includeImported=false 排除 imported | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 201 | `async_method_naming` | GetImportableList_IncludesImportedWhenFlagTrue | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 203 | `chinese_comment_or_message` | // UT-QBI-04: includeImported=true 包含 imported | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 231 | `async_method_naming` | GetImportableList_FailedStatusNotExcluded | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 233 | `chinese_comment_or_message` | // UT-QBI-05: failed 状态不被排除 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 255 | `async_method_naming` | GetImportableList_PageSizeCappedToMax | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 257 | `chinese_comment_or_message` | // UT-QBI-06: 分页参数修正(pageSize > 100) | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 270 | `async_method_naming` | GetImportableList_OrderedByParsedAtDesc | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 272 | `chinese_comment_or_message` | // UT-QBI-07: 按 parsed_at DESC 排序 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 293 | `async_method_naming` | GetBlocks_ParseNotFound_ThrowsKeyNotFoundException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 295 | `chinese_comment_or_message` | // UT-QBI-08: parse 不存在抛 KeyNotFoundException | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 305 | `async_method_naming` | GetBlocks_ParseNotParsed_ThrowsInvalidOperationException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 307 | `chinese_comment_or_message` | // UT-QBI-09: parse 状态非 parsed 抛 InvalidOperationException | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 321 | `async_method_naming` | GetBlocks_PageIdZeroFilterWorks | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 323 | `chinese_comment_or_message` | // UT-QBI-10: pageId=0 过滤有效(0 是有效值) | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 340 | `async_method_naming` | GetBlocks_BlockTypeFilterWorks | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 342 | `chinese_comment_or_message` | // UT-QBI-11: blockType 过滤 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 358 | `async_method_naming` | GetBlocks_ImageBlockReturnsImageFields | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 360 | `chinese_comment_or_message` | // UT-QBI-12: image block 返回 imageName/imagePath/imageUrl | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 380 | `async_method_naming` | GetBlocks_TextBlockHasNullImageFields | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 382 | `chinese_comment_or_message` | // UT-QBI-13: text block 的图片字段为 null | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 397 | `async_method_naming` | GetBlocks_ImageIdSetButImageMissing_ReturnsNullFields | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 399 | `chinese_comment_or_message` | // UT-QBI-14: image_id 有值但 image 记录缺失 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 416 | `async_method_naming` | GetBlocks_OssPresignedUrlFailure_ReturnsNullUrl | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 418 | `chinese_comment_or_message` | // UT-QBI-15: OSS 生成 presigned URL 失败时 imageUrl=null | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 438 | `async_method_naming` | GetBlocks_PageSizeCappedToMax | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 440 | `chinese_comment_or_message` | // UT-QBI-16: 分页参数修正(pageSize > 200) | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 454 | `async_method_naming` | GetBlocks_BlockDataValidJson_ReturnsObject | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 456 | `chinese_comment_or_message` | // UT-QBI-17: blockData 合法 JSON 解析为对象 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 467 | `null_forgiving_operator` | count=1: var element = (JsonElement)items[0].BlockData!; | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 474 | `async_method_naming` | GetBlocks_BlockDataInvalidJson_ReturnsNull | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 476 | `chinese_comment_or_message` | // UT-QBI-18: blockData 非法 JSON 返回 null | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 490 | `async_method_naming` | GetImageBlob_ImageNotFound_ThrowsKeyNotFoundException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 492 | `chinese_comment_or_message` | // UT-QBI-19: imageId 不存在抛 KeyNotFoundException | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 502 | `async_method_naming` | GetImageBlob_OssDownloadFailure_Throws | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 504 | `chinese_comment_or_message` | // UT-QBI-20: OSS 下载失败抛异常 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 521 | `async_method_naming` | GetImageBlob_Success_ReturnsBlob | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 523 | `chinese_comment_or_message` | // UT-QBI-21: 成功返回 ParseImageBlob | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 547 | `async_method_naming` | UpsertImportStatus_ParseNotFound_ThrowsKeyNotFoundException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 549 | `chinese_comment_or_message` | // UT-QBI-22: parse 不存在抛 KeyNotFoundException | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 560 | `async_method_naming` | UpsertImportStatus_InvalidStatus_ThrowsArgumentException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 562 | `chinese_comment_or_message` | // UT-QBI-23: status 非法抛 ArgumentException | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 576 | `chinese_comment_or_message` | // UT-QBI-24: 首次插入 imported | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 600 | `null_forgiving_operator` | count=1: captured!.ParseId.Should().Be(parse.Id); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 612 | `chinese_comment_or_message` | // UT-QBI-25: 首次插入 failed | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 632 | `async_method_naming` | UpsertImportStatus_ImportedToImported_ThrowsInvalidOperationException | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 634 | `chinese_comment_or_message` | // UT-QBI-26: imported → imported 抛异常 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 660 | `chinese_comment_or_message` | // UT-QBI-27: imported → failed 成功覆盖 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 691 | `null_forgiving_operator` | count=1: updated!.Status.Should().Be(ParseImportStatus.Failed); | 减少 `!` 使用，添加显式空值检查或改用可空引用类型注解 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 699 | `chinese_comment_or_message` | // UT-QBI-28: failed → imported 成功覆盖 | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/QuestionBankImportServiceTests.cs` | 724 | `chinese_comment_or_message` | // UT-QBI-29: failed → failed 成功覆盖(允许重复 failed) | 将注释/消息改为英文（业务域值、HTTP 用户消息除外） |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 31 | `async_method_naming` | ExactSearchAsync_DelegatesToSearchIndexService | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 53 | `async_method_naming` | ExactSearchAsync_PropagatesFilterToIndexService | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 77 | `async_method_naming` | ExactSearchAsync_PropagatesPageToken | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 95 | `async_method_naming` | ExactSearchAsync_WhenIndexServiceThrows_ReturnsEmptyResults | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 112 | `async_method_naming` | ExactSearchAsync_ReturnsEmptyResultsWhenNoMatch | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 131 | `async_method_naming` | ExactSearchAsync_PropagatesMinerUFilterToIndexService | 异步方法名追加 `Async` 后缀 |
| `src/services/ruoyu.doclibrary/src/Tests/Ruoyu.Study.DocLibrary.Tests/SearchDomainServiceTests.cs` | 158 | `async_method_naming` | ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty | 异步方法名追加 `Async` 后缀 |

## 未通过静态扫描检测到的项

- `unused_using`：`dotnet format style <sln> --verify-no-changes --diagnostics IDE0005` 未报告任何文件需要修改，说明显式 `using` 指令基本被使用。
- 另外，`dotnet format analyzers` 在范围内还报告以下性能/风格提示（未写入上表）：
  - `DocumentFileRepository.cs(36,38)` CA1862：string.Contains 未使用 StringComparison。
  - `DocumentAnalysisOptions.cs(43,19)` CA1822：成员 Temperature 可标记为 static。
  - `PdfSplitServiceTests.cs(21,20)` CA1822 / CA1859。
  - `DocumentFileDeleteCleanupTests.cs(91,48)` CA1861。
