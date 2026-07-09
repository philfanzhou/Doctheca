# 02-SPEC — DocumentExport 详细规格

## 1. HTTP 端点

### 1.1 ExportMarkdown — 文件级 Markdown 导出（ZIP）

```
GET /admin/document-files/{id:guid}/export/markdown
```

**请求参数**（路径）:`id` — `document_files` 表主键

**处理流程**:
1. `fileService.GetByIdAsync(id)` → null 则 404
2. `parseService.GetLatestByFileIdAsync(id)` → null 或 `Status != parsed` 则 422
3. `parseService.GetImagesByParseIdAsync(parse.Id)` — 获取图片列表
4. `MarkdownExportHelper.ReplaceImagePathsRelative(markdownContent, images)` — 路径改写
5. `MarkdownExportHelper.BuildMarkdownZipAsync(fileName, markdown, images, ossService, logger)` — 构建 ZIP
6. `Results.Stream(zipStream, "application/zip", "{name}_markdown.zip")`

**响应**:
- 200:`application/zip` 流，文件名 `{fileName}_markdown.zip`
- 404:`{ "success": false, "message": "File not found", "errorCode": "DOCLIBRARY_FILE_NOT_FOUND" }`
- 422:`{ "success": false, "message": "File is not parsed yet", "errorCode": "DOCLIBRARY_FILE_NOT_PARSED" }`

### 1.2 ExportHtml — 文件级 HTML 导出

```
GET /admin/document-files/{id:guid}/export/html
```

**处理流程**:
1. 同 1.1 步骤 1-2
2. `parseService.GetImagesByParseIdAsync(parse.Id)`
3. `await MarkdownExportHelper.ReplaceImagePathsBase64Async(markdownContent, images, ossService, logger)` — 图片 Base64 内联
4. `MarkdownExportHelper.BuildHtmlStream(fileName, markdown)` — 转为 HTML
5. `Results.Stream(htmlStream, "text/html", "{name}.html")`

**响应**:
- 200:`text/html` 流，文件名 `{fileName}.html`
- 404 / 422：同 1.1

### 1.3 ExportParseMarkdown — 解析级 Markdown 导出（ZIP）

```
GET /admin/document-parses/{parseId:guid}/export/markdown
```

**请求参数**（路径）:`parseId` — `document_parses` 表主键

**处理流程**:
1. `parseService.GetByIdAsync(parseId)` → null 则 404
2. `Status != parsed` 则 422
3. `fileService.GetByIdAsync(parse.DocumentFileId)` → `fileName = file?.FileName ?? "document"`
4. 同 1.1 步骤 3-6

**响应**:
- 200:`application/zip` 流
- 404:`{ "success": false, "message": "Parse record not found", "errorCode": "DOCLIBRARY_PARSE_NOT_FOUND" }`
- 422:`{ "success": false, "message": "Parse record is not parsed yet", "errorCode": "DOCLIBRARY_PARSE_NOT_PARSED" }`

### 1.4 ExportParseHtml — 解析级 HTML 导出

```
GET /admin/document-parses/{parseId:guid}/export/html
```

**处理流程**:同 1.3 的解析记录校验 + 1.2 的 HTML 构建流程。

**响应**:同 1.2，错误码同 1.3。

## 2. 路由注册

```csharp
// DocumentExportEndpoints.cs
public static WebApplication MapDocumentExportEndpoints(this WebApplication app)
{
    var fileGroup = app.MapGroup("/admin/document-files");
    fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
    fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);

    var parseGroup = app.MapGroup("/admin/document-parses");
    parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown);
    parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);

    return app;
}
```

`Program.cs` 中注册:app.MapDocumentExportEndpoints();

## 3. 图片路径三种模式规格

### 3.1 ReplaceImagePathsRelative（同步）

将 OSS 绝对路径改写为相对路径，用于 Markdown/ZIP 导出。

```
输入:markdown 图片段，替换规则（每张 img）:
  "({img.ImagePath})"            → "(images/{img.ImageName})"
  "src=\"{img.ImagePath}\""      → "src=\"images/{img.ImageName}\""
  "src='{img.ImagePath}'"        → "src='images/{img.ImageName}'"
```

- **无 OSS 调用**，纯字符串替换
- **不匹配的路径保持不变**

### 3.2 ReplaceImagePathsBase64Async（异步）

下载图片并转为 Base64 data URI，用于 HTML 导出。

```
对每张 img:
  1. ossService.DownloadAsync(img.ImagePath) → Stream
  2. CopyTo MemoryStream → byte[]
  3. Convert.ToBase64String → base64
  4. dataUri = $"data:{img.ContentType};base64,{base64}"
  5. 替换（6 种格式）:
     "({img.ImagePath})"                        → "({dataUri})"
     "src=\"{img.ImagePath}\""                  → "src=\"{dataUri}\""
     "src='{img.ImagePath}'"                    → "src='{dataUri}'"
     "(images/{img.ImageName})"                 → "({dataUri})"
     "src=\"images/{img.ImageName}\""           → "src=\"{dataUri}\""
     "src='images/{img.ImageName}'"             → "src='{dataUri}'"
  catch Exception → logger.LogWarning, 跳过
```

- 替换格式覆盖 OSS 路径、相对路径、单双引号，保证已改写路径也能再次处理
- `img.ContentType` 默认 `"image/jpeg"`（`DocumentParseImageModel` 默认值）

### 3.3 ReplaceImagePathsPresignedAsync（异步，当前未接入端点）

生成预签名 URL，供浏览器查看。

```
对每张 img:
  1. ossService.GetPresignedUrlAsync(img.ImagePath, 3600) → presignedUrl（1 小时有效期）
  2. 替换（4 种格式）:
     "({img.ImagePath})"                        → "({presignedUrl})"
     "(images/{img.ImageName})"                 → "({presignedUrl})"
     "src=\"{img.ImagePath}\""                  → "src=\"{presignedUrl}\""
     "src=\"images/{img.ImageName}\""           → "src=\"{presignedUrl}\""
  catch Exception → logger.LogWarning, 跳过
```

> **注意**:此方法当前由端点代码调用链**未实际接入**。作为 helper 能力保留，未来可在"浏览器内预览"场景启用。接入时不影响现有 4 个端点行为。

## 4. ZIP 打包规格 — BuildMarkdownZipAsync

```
输入:fileName, markdownContent, images, ossService, logger
输出:MemoryStream（Position = 0）

1. new MemoryStream()
2. new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)
3. 创建 entry "{Path.GetFileNameWithoutExtension(fileName)}.md"（CompressionLevel.Optimal）
   - StreamWriter 写入 markdownContent
4. 对每张 img:
   - ossService.DownloadAsync(img.ImagePath)
   - 创建 entry $"images/{img.ImageName}"（CompressionLevel.Fastest）
   - CopyToAsync 写入
   catch Exception → logger.LogWarning, 跳过
5. ms.Position = 0; return ms
```

- 文本用 Optimal（压缩率高），图片用 Fastest（图片已压缩，无需高压缩率）
- `leaveOpen: true` 确保 ZipArchive 关闭后 MemoryStream 仍可用

## 5. HTML 转换规格 — BuildHtmlStream

```
输入:fileName, markdownContent
输出:MemoryStream

1. Markdig.Markdown.ToHtml(markdownContent) → htmlBody
2. 构建完整 HTML 文档:
   - "<!DOCTYPE html>"
   - "<html lang=\"zh-CN\">"
   - <head>:charset UTF-8, viewport, title=HtmlEncode(fileName), 内联 <style>
   - <body>:htmlBody
3. Encoding.UTF8.GetBytes → MemoryStream
```

**内联 CSS** 覆盖:`body`(字体/宽度/行高/颜色)、`img`(max-width)、`table`/`th`/`td`(边框)、`blockquote`、`code`、`pre`。

## 6. 实现步骤

### 6.1 ExportMarkdownCore（共享）

```
1. parseService.GetImagesByParseIdAsync(parse.Id) → images
2. MarkdownExportHelper.ReplaceImagePathsRelative(parse.MarkdownContent ?? "", images) → markdownContent
3. MarkdownExportHelper.BuildMarkdownZipAsync(fileName, markdownContent, images, ossService, logger) → zipStream
4. zipFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_markdown.zip"
5. Results.Stream(zipStream, "application/zip", zipFileName)
```

### 6.2 ExportHtmlCore（共享）

```
1. parseService.GetImagesByParseIdAsync(parse.Id) → images
2. await MarkdownExportHelper.ReplaceImagePathsBase64Async(parse.MarkdownContent ?? "", images, ossService, logger) → markdownContent
3. MarkdownExportHelper.BuildHtmlStream(fileName, markdownContent) → htmlStream
4. htmlFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.html"
5. Results.Stream(htmlStream, "text/html", htmlFileName)
```

### 6.3 解析级导出文件名回退

解析级导出时若关联文件已删除，`file?.FileName ?? "document"` — 保证 ZIP/HTML 文件名不回退为空。

## 7. 错误处理

| 场景 | HTTP | 错误码 | 处理 |
|------|------|--------|------|
| 文件不存在 | 404 | `DOCLIBRARY_FILE_NOT_FOUND` | 文件级端点 |
| 解析记录不存在 | 404 | `DOCLIBRARY_PARSE_NOT_FOUND` | 解析级端点 |
| 文件未解析 | 422 | `DOCLIBRARY_FILE_NOT_PARSED` | 文件级端点 |
| 解析记录未解析 | 422 | `DOCLIBRARY_PARSE_NOT_PARSED` | 解析级端点 |
| 单张图片 OSS 下载失败 | — | — | 记 Warning 日志，跳过该张 |
| 单张图片预签名失败 | — | — | 记 Warning 日志，跳过该张 |

## 8. 测试策略

### 8.1 单元测试 (UT)

纯逻辑测试，不依赖 HTTP / OSS / 数据库。位于 `DocumentExportLogicTests.cs`，共 12 个测试。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DE-01 | `MarkdownPathReplacement_ReplacesS3PathWithRelativePath` | FR-05（相对路径） |
| UT-DE-02 | `MarkdownPathReplacement_ReplacesSrcAttribute` | FR-05（双引号 src） |
| UT-DE-03 | `MarkdownPathReplacement_ReplacesSrcAttribute_SingleQuotes` | FR-05（单引号 src） |
| UT-DE-04 | `MarkdownPathReplacement_ReplacesMixedQuoteFormats` | FR-05（混合格式） |
| UT-DE-05 | `MarkdownPathReplacement_HandlesMultipleImages` | FR-05（多图） |
| UT-DE-06 | `MarkdownPathReplacement_DoesNotReplaceIfPathNotFound` | FR-05（路径不匹配） |
| UT-DE-07 | `HtmlExport_ReplacesS3PathWithDataUri` | FR-05（Base64） |
| UT-DE-08 | `Markdig_ConvertsMarkdownToHtml` | FR-02（Markdig 基础转换） |
| UT-DE-09 | `Markdig_ConvertsMarkdownWithImageToHtml` | FR-02（Markdig 图片转换） |
| UT-DE-10 | `HtmlTemplate_GeneratesValidHtmlDocument` | FR-02（HTML 模板） |
| UT-DE-11 | `ZipExport_ContainsMarkdownAndImages` | FR-01（ZIP 结构） |
| UT-DE-12 | `Base64Conversion_RoundTripsCorrectly` | FR-05（Base64 往返） |

### 8.2 集成测试（未实现）

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-DE-01 | 文件级 Markdown 导出返回有效 ZIP | AC-01 |
| IT-DE-02 | 文件级 HTML 导出返回含 Base64 的 HTML | AC-02 |
| IT-DE-03 | 解析级导出与文件级导出结果一致 | AC-03 |
| IT-DE-04 | 文件不存在返回 404 | AC-04 |
| IT-DE-05 | 解析记录不存在返回 404 | AC-05 |
| IT-DE-06 | 未解析返回 422 | AC-06 |

## 9. 影响范围

| 组件 | 影响 |
|------|------|
| `DocumentExportEndpoints` | 新增 4 个端点 + `MapDocumentExportEndpoints` |
| `MarkdownExportHelper` | 新增 5 个静态方法 |
| `Program.cs` | 注册 `MapDocumentExportEndpoints` |
| `document_parses` 表 | 只读，不修改 |
| `document_parse_images` 表 | 只读，不修改 |
| `document_files` 表 | 只读，不修改 |
| OSS | 只读下载，不写入 |

## 10. 部署脚本

**不修改**。导出功能复用现有 OSS / 数据库 / 解析链路配置，无需新增环境变量或配置节。
