# DocumentExport — 测试文档

## 单元测试（DocumentExportLogicTests.cs）

共 12 个纯逻辑单元测试，位于 `DocumentExportLogicTests.cs`。测试覆盖图片路径替换、Markdig 转换、HTML 模板、ZIP 结构、Base64 往返，不依赖 HTTP / OSS / 数据库。

| # | 测试方法 | 覆盖 | 状态 |
|---|---------|------|------|
| UT-DE-01 | `MarkdownPathReplacement_ReplacesS3PathWithRelativePath` | FR-05（相对路径基础替换） | completed |
| UT-DE-02 | `MarkdownPathReplacement_ReplacesSrcAttribute` | FR-05（双引号 src 属性替换） | completed |
| UT-DE-03 | `MarkdownPathReplacement_ReplacesSrcAttribute_SingleQuotes` | FR-05（单引号 src 属性替换） | completed |
| UT-DE-04 | `MarkdownPathReplacement_ReplacesMixedQuoteFormats` | FR-05（混合格式 + 无残留绝对路径） | completed |
| UT-DE-05 | `MarkdownPathReplacement_HandlesMultipleImages` | FR-05（多图批量替换） | completed |
| UT-DE-06 | `MarkdownPathReplacement_DoesNotReplaceIfPathNotFound` | FR-05（路径不匹配时保持不变） | completed |
| UT-DE-07 | `HtmlExport_ReplacesS3PathWithDataUri` | FR-05（Base64 data URI 替换） | completed |
| UT-DE-08 | `Markdig_ConvertsMarkdownToHtml` | FR-02（Markdown→HTML 基础：标题/加粗/列表） | completed |
| UT-DE-09 | `Markdig_ConvertsMarkdownWithImageToHtml` | FR-02（Markdown→HTML 图片：img/alt/src） | completed |
| UT-DE-10 | `HtmlTemplate_GeneratesValidHtmlDocument` | FR-02（自包含 HTML 文档结构） | completed |
| UT-DE-11 | `ZipExport_ContainsMarkdownAndImages` | FR-01（ZIP 内含 .md 与 images/） | completed |
| UT-DE-12 | `Base64Conversion_RoundTripsCorrectly` | FR-05（Base64 编解码往返 + data URI 前缀） | completed |

### FR/AC 映射

- FR-01（Markdown/ZIP 导出）→ UT-DE-11
- FR-02（HTML 导出）→ UT-DE-08、UT-DE-09、UT-DE-10
- FR-03（解析级 Markdown 导出）→ 与 FR-01 共享 `ExportMarkdownCore`，同 UT-DE-11
- FR-04（解析级 HTML 导出）→ 与 FR-02 共享 `ExportHtmlCore`，同 UT-DE-08/09/10
- FR-05（三种图片路径模式）→ UT-DE-01 至 UT-DE-07、UT-DE-12
- FR-06 / FR-07（校验与容错）→ 未覆盖（需集成测试）

## 集成测试（未实现）

端点层（`DocumentExportEndpoints`）与共享核心（`ExportMarkdownCore` / `ExportHtmlCore`）当前无集成测试，需补充以覆盖 FR-06 / FR-07 及 AC-04 至 AC-09。

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-DE-01 | 文件级 Markdown 导出返回有效 ZIP，Entry 含 .md 与 images/ | AC-01, AC-08 |
| IT-DE-02 | 文件级 HTML 导出返回含 Base64 img 的 HTML | AC-02, AC-09 |
| IT-DE-03 | 解析级导出与文件级导出结果一致（同一 parseId） | AC-03 |
| IT-DE-04 | 文件不存在返回 404 + DOCLIBRARY_FILE_NOT_FOUND | AC-04 |
| IT-DE-05 | 解析记录不存在返回 404 + DOCLIBRARY_PARSE_NOT_FOUND | AC-05 |
| IT-DE-06 | 未解析返回 422 + DOCLIBRARY_FILE_NOT_PARSED / DOCLIBRARY_PARSE_NOT_PARSED | AC-06 |
| IT-DE-07 | 单张图片 OSS 下载失败不阻塞导出，记 Warning 日志 | AC-07 |

## 运行方式

```bash
# 运行本模块单元测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentExport"

# 运行所有测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests
```

## 覆盖率目标

- `MarkdownExportHelper` 代码行覆盖率 ≥ 80%
- `DocumentExportEndpoints` 端点层建议集成测试覆盖前置校验分支
