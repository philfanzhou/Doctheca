# DocumentExport — 任务列表

> **本模块所有任务已完成。** 以下为历史记录。

## 已完成任务

| ID | 任务 | 状态 |
|----|------|------|
| DE-01 | 创建 `MarkdownExportHelper`（ReplaceImagePathsRelative / ReplaceImagePathsBase64Async / ReplaceImagePathsPresignedAsync / BuildMarkdownZipAsync / BuildHtmlStream） | completed |
| DE-02 | 创建 `DocumentExportEndpoints`（ExportMarkdown / ExportHtml / ExportParseMarkdown / ExportParseHtml + MapDocumentExportEndpoints） | completed |
| DE-03 | 实现 `ExportMarkdownCore`（共享 Markdown/ZIP 核心） | completed |
| DE-04 | 实现 `ExportHtmlCore`（共享 HTML 核心） | completed |
| DE-05 | 实现文件级端点前置校验（文件存在 / 已解析） | completed |
| DE-06 | 实现解析级端点前置校验（解析记录存在 / 已解析 / 文件名回退） | completed |
| DE-07 | Program.cs 注册 `MapDocumentExportEndpoints` | completed |
| DE-08 | 单元测试 `DocumentExportLogicTests`（12 tests） | completed |

## 命令速查

```bash
# 构建
dotnet build Doctheca.sln --configuration Release

# 测试（本模块）
dotnet test src/Tests/Doctheca.Tests --filter "FullyQualifiedName~DocumentExport"

# 测试（全部）
dotnet test src/Tests/Doctheca.Tests
```
