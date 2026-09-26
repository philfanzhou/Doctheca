# DocumentExport — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 端点静态类 | `XxxEndpoints` | `DocumentExportEndpoints` |
| Helper 静态类 | `XxxHelper`，internal | `MarkdownExportHelper` |
| 图片模型 | `DocumentParseImageModel` | — |
| 方法 | PascalCase，Async 后缀 | `ReplaceImagePathsBase64Async`、`BuildMarkdownZipAsync` |
| 参数 | camelCase | `markdownContent`、`ossService`、`parseId` |
| 局部变量 | camelCase | `htmlBody`、`zipFileName`、`presignedUrl` |

## 方法命名约定（MarkdownExportHelper）

- `ReplaceImagePathsRelative` — 同步，无 IO
- `ReplaceImagePathsBase64Async` — 异步，涉及 OSS 下载
- `ReplaceImagePathsPresignedAsync` — 异步，涉及预签名生成
- `BuildMarkdownZipAsync` — 异步，涉及 OSS 下载
- `BuildHtmlStream` — 同步，纯内存变换

异步方法一律 `Async` 后缀；同步方法不附加 `Sync` 后缀。

## 日志约定

- 图片下载失败：`logger.LogWarning(ex, "Failed to download image for export: {ImagePath}", img.ImagePath)`
- Base64 下载失败：`logger.LogWarning(ex, "Failed to download image for HTML export: {ImagePath}", img.ImagePath)`
- 预签名失败：`logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath)`

所有图片级失败使用 `LogWarning`（可恢复，不阻塞导出），附带异常对象与 `ImagePath` 定位。

## 错误消息约定

HTTP 响应消息为中文（面向终端用户），错误码为英文常量：

| 错误码 | 消息 | 场景 |
|--------|------|------|
| `DOCTHECA_FILE_NOT_FOUND` | `File not found` | 文件级端点 |
| `DOCTHECA_FILE_NOT_PARSED` | `File is not parsed yet` | 文件级端点 |
| `DOCTHECA_PARSE_NOT_FOUND` | `Parse record not found` | 解析级端点 |
| `DOCTHECA_PARSE_NOT_PARSED` | `Parse record is not parsed yet` | 解析级端点 |

响应统一格式：`{ "success": false, "message": "...", "errorCode": "..." }`。

## 空值与容错语义

| 值 / 场景 | 语义 |
|-----------|------|
| `parse.MarkdownContent ?? ""` | 解析内容为空时以空字符串兜底，不抛 null |
| `file?.FileName ?? "document"` | 解析级导出文件已删除时文件名回退为 `"document"` |
| 单张图片 OSS 下载失败 | 跳过该张，不阻塞整份导出 |
| 路径在 markdown 中不存在 | `Replace` 不生效，保持原文 |

## 路径处理约定

- Markdown 中图片引用匹配三种格式：`(<path>)`、`src="<path>"`、`src='<path>'`
- 相对路径统一指向 `images/{ImageName}`（小写目录名 + 正斜杠）
- Base64 data URI 格式：`data:{ContentType};base64,{base64}`（MimeType 取自 `img.ContentType`，默认 `image/jpeg`）

## ZIP 打包约定

- Markdown 条目：`{fileName}.md`，`CompressionLevel.Optimal`
- 图片条目：`images/{ImageName}`，`CompressionLevel.Fastest`
- `ZipArchive` 使用 `leaveOpen: true`，确保关闭后 MemoryStream 仍可用
- 返回前重置 `ms.Position = 0`

## HTML 模板约定

- 文档类型：`<!DOCTYPE html>`
- 语言：`<html lang="zh-CN">`
- 编码：`charset=UTF-8`
- 标题：`WebUtility.HtmlEncode(fileName)`（防 XSS）
- 样式：内联 `<style>`，不引入外部 CSS
- 转换：`Markdig.Markdown.ToHtml(markdownContent)`

## Dispose 约定

- `using var imgStream = await ossService.DownloadAsync(...)` — 每张图片流单独释放
- `ZipArchive` 包裹在 `using` 中，`leaveOpen: true` 仅对底层 `MemoryStream` 放行
- `StringBuilder` / `MemoryStream` 无需显式 Dispose

## 端点注册约定

- 使用 `MapGroup` 分组：`/admin/document-files`、`/admin/document-parses`
- 路径参数约束：`{id:guid}`、`{parseId:guid}`
- 导出端点要求 `DocthecaAdmin` 策略和 Identity `role=admin`
