# 01-FEATURE — DocumentExport 文档导出

## 功能概述

将已解析文档（`document_parses` 状态为 `parsed`）的 Markdown 内容导出为可下载文件。支持两种导出维度（文件级 / 解析级）与两种导出格式（Markdown / HTML），共 4 个端点。Markdown 导出将 Markdown 正文与图片打包为 ZIP；HTML 导出将图片 Base64 内联后通过 Markdig 转为自包含 HTML 文档。

## 背景

解析产物的图片存在两种来源（ADR-0009）：**存量解析**的 `markdown_content` 引用 OSS 绝对路径（如 `documents/mineru/task-123/abc.jpg`），字节从 OSS 读取；**新解析**的 Markdown 引用相对路径（`images/<name>`），字节经 `ParseImageContentSource` 从 StructaDoc Asset 流式读取。直接分发的 Markdown 在离线环境下图片无法加载。本功能：

1. 提供文件级导出（按 `document_file_id` 取最新解析记录）
2. 提供解析级导出（直接按 `parse_id` 指定某次解析）
3. Markdown 导出将图片路径改写为相对路径（`images/<name>`）并打包为 ZIP，保证离线可用
4. HTML 导出将图片下载后 Base64 内联，生成单文件自包含 HTML

## 用户故事

- **作为老师**:我上传讲义并解析完成后，可以一键下载 Markdown 源码包（含图片），方便二次编辑
- **作为老师**:我可以直接下载 HTML 版本，浏览器打开即可查看，图片内联无需联网
- **作为运维**:我可以直接指定某次解析记录导出，不依赖"最新一次解析"
- **作为运维**:导出时如果图片来源（OSS 或 StructaDoc）下载某张图片失败，该张图片跳过，不阻塞整份导出

## 功能需求

### FR-01:文件级 Markdown 导出（ZIP）
- `GET /admin/document-files/{id}/export/markdown`
- 取该文件最新解析记录，将 `MarkdownContent` 中图片引用统一改写为相对路径 `images/<ImageName>`，打包为 `{fileName}_markdown.zip`
- ZIP 内包含一个 `.md` 文件（Optimal 压缩）和一个 `images/` 目录（Fastest 压缩）

### FR-02:文件级 HTML 导出
- `GET /admin/document-files/{id}/export/html`
- 取该文件最新解析记录，将图片下载后 Base64 内联到 Markdown，再通过 `Markdig.Markdown.ToHtml` 转为 HTML，返回 `{fileName}.html`

### FR-03:解析级 Markdown 导出（ZIP）
- `GET /admin/document-parses/{parseId}/export/markdown`
- 直接按 `parseId` 取解析记录，逻辑同 FR-01

### FR-04:解析级 HTML 导出
- `GET /admin/document-parses/{parseId}/export/html`
- 直接按 `parseId` 取解析记录，逻辑同 FR-02

### FR-05:图片路径三种处理模式
- **相对路径**（`ReplaceImagePathsRelative`）：用于 Markdown/ZIP 导出，路径改写为 `images/<ImageName>`
- **Base64 内联**（`ReplaceImagePathsBase64Async`）：用于 HTML 导出，下载图片转为 `data:{ContentType};base64,...`
- **浏览器 URL**（`ReplaceImagePathsAsync` + URL resolver）：详情页使用——存量解析生成 1 小时有效期 OSS 预签名 URL；新解析生成代理端点 URL `/admin/document-parses/{parseId}/images/{imageId}/content`

### FR-06:导出前置校验
- 文件不存在 → 404 `DOCTHECA_FILE_NOT_FOUND`
- 解析记录不存在 → 404 `DOCTHECA_PARSE_NOT_FOUND`
- 解析状态非 `parsed` → 422 `DOCTHECA_FILE_NOT_PARSED` / `DOCTHECA_PARSE_NOT_PARSED`

### FR-07:图片下载失败容错
- 单张图片下载失败（OSS 或 StructaDoc）→ 记 Warning 日志，跳过该张，不阻塞整份导出
- 浏览器 URL 生成失败 → 记 Warning 日志，跳过该张

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | 文件级 Markdown 导出返回 ZIP，内含 `.md` 与 `images/` |
| AC-02 | 文件级 HTML 导出返回自包含 HTML，图片以 Base64 内联 |
| AC-03 | 解析级导出与文件级导出结果一致（同一 parseId） |
| AC-04 | 文件不存在返回 404 + `DOCTHECA_FILE_NOT_FOUND` |
| AC-05 | 解析记录不存在返回 404 + `DOCTHECA_PARSE_NOT_FOUND` |
| AC-06 | 解析未完成返回 422 + 对应错误码 |
| AC-07 | 单张图片下载失败（OSS 或 StructaDoc）不阻塞导出，记 Warning 日志 |
| AC-08 | Markdown 导出中图片路径为相对路径 `images/<ImageName>` |
| AC-09 | HTML 导出中图片为 `data:{ContentType};base64,...` 格式 |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | 导出为流式返回（`Results.Stream`），不落地磁盘 |
| NFR-02 | ZIP/HTML 在内存中构建（`MemoryStream`），请求结束后释放 |
| NFR-03 | 图片下载失败为 best-effort，不阻塞整份导出 |
| NFR-04 | 仅 HTTP，无 gRPC |
| NFR-05 | 导出不改变解析状态，不触发重新解析 |
| NFR-06 | 日志记录导出文件名、parseId、图片下载失败原因 |

## 数据来源

- **Markdown 内容**:`document_parses.markdown_content`
- **图片列表**:`document_parse_images`（按 `parse_id` 查询）
- **图片二进制**:OSS（`IOssService.DownloadAsync`）
- **文件名校验**:`document_files.file_name`（解析级导出时若文件不存在则回退为 `"document"`）

## 接口清单

| 组件 | 修改 |
|------|------|
| `DocumentExportEndpoints` | 新增 4 个端点 + `MapDocumentExportEndpoints` |
| `MarkdownExportHelper` | 新增 `ReplaceImagePathsRelative` / `ReplaceImagePathsBase64Async` / `ReplaceImagePathsPresignedAsync` / `BuildMarkdownZipAsync` / `BuildHtmlStream` |
| `IDocumentParseService` | 复用现有 `GetByIdAsync` / `GetLatestByFileIdAsync` / `GetImagesByParseIdAsync` |
| `IDocumentFileService` | 复用现有 `GetByIdAsync` |
| `IOssService` | 复用现有 `DownloadAsync` / `GetPresignedUrlAsync` |
| `Program.cs` | 注册 `MapDocumentExportEndpoints` |
