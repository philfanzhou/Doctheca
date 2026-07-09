# 03-DESIGN — DocumentExport 设计说明

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentParseModel.cs              # 解析记录（MarkdownContent / Id / DocumentFileId）
│   │   │   ├── DocumentParseImageModel.cs         # 图片模型（ImageName / ImagePath / ContentType）
│   │   │   ├── DocumentParseStatus.cs             # 状态常量（Pending / Parsing / Parsed / Failed）
│   │   │   └── DocumentFileModel.cs               # 文件模型（FileName）
│   │   └── Services/
│   │       ├── IDocumentParseService.cs           # 解析服务接口
│   │       ├── DocumentParseService.cs            # 解析服务实现
│   │       ├── IDocumentFileService.cs            # 文件服务接口
│   │       └── DocumentFileService.cs             # 文件服务实现
│   ├── Service/
│   │   ├── MarkdownExportHelper.cs                # 导出核心逻辑（图片路径处理 / ZIP / HTML）
│   │   └── Endpoints/
│   │       └── DocumentExportEndpoints.cs         # 4 个导出端点
│   └── Host/
│       └── Program.cs                             # 注册 MapDocumentExportEndpoints
└── docs/modules/DocumentExport/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (本文档)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名

### IDocumentParseService（复用，导出涉及的方法）

```csharp
Task<DocumentParseModel?> GetByIdAsync(Guid id);
Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
```

### IDocumentFileService（复用）

```csharp
Task<DocumentFileModel?> GetByIdAsync(Guid id);
```

### IOssService（复用，ruoyu.common）

```csharp
Task<Stream> DownloadAsync(string objectPath);
Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
```

### MarkdownExportHelper（新增，internal static）

```csharp
public static string ReplaceImagePathsRelative(string markdown, IReadOnlyList<DocumentParseImageModel> images);
public static Task<string> ReplaceImagePathsBase64Async(string markdown, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static Task<string> ReplaceImagePathsPresignedAsync(string markdown, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static Task<MemoryStream> BuildMarkdownZipAsync(string fileName, string markdownContent, IReadOnlyList<DocumentParseImageModel> images, IOssService ossService, ILogger logger);
public static MemoryStream BuildHtmlStream(string fileName, string markdownContent);
```

### HTTP 端点

```
GET /admin/document-files/{id}/export/markdown      → ZIP（相对路径）
GET /admin/document-files/{id}/export/html          → HTML（Base64 内联）
GET /admin/document-parses/{parseId}/export/markdown → ZIP（相对路径）
GET /admin/document-parses/{parseId}/export/html     → HTML（Base64 内联）
```

## 数据流

### 文件级 Markdown 导出（ZIP）

```
HTTP Request
  → DocumentExportEndpoints.ExportMarkdown
  → fileService.GetByIdAsync(id)                    [校验文件存在]
  → parseService.GetLatestByFileIdAsync(id)         [校验已解析]
  → parseService.GetImagesByParseIdAsync(parse.Id)  [获取图片列表]
  → MarkdownExportHelper.ReplaceImagePathsRelative  [同步改写路径]
  → MarkdownExportHelper.BuildMarkdownZipAsync      [下载图片 + 打包 ZIP]
  → Results.Stream(application/zip, "{name}_markdown.zip")
```

### 文件级 HTML 导出

```
HTTP Request
  → DocumentExportEndpoints.ExportHtml
  → fileService.GetByIdAsync(id)                    [校验文件存在]
  → parseService.GetLatestByFileIdAsync(id)         [校验已解析]
  → parseService.GetImagesByParseIdAsync(parse.Id)  [获取图片列表]
  → MarkdownExportHelper.ReplaceImagePathsBase64Async [下载图片 + Base64 内联]
  → MarkdownExportHelper.BuildHtmlStream            [Markdig 转 HTML + 模板]
  → Results.Stream(text/html, "{name}.html")
```

### 解析级导出

与文件级共享 `ExportMarkdownCore` / `ExportHtmlCore`，仅入口校验不同：
- 直接按 `parseId` 取解析记录
- 文件名从 `document_files` 回查，缺失则回退为 `"document"`

## 设计决策

### 为何支持三种图片路径模式

| 模式 | 适用场景 | 选择理由 |
|------|---------|---------|
| 相对路径 | Markdown/ZIP 导出 | 离线可用，ZIP 内 `images/` 目录与 `.md` 相对位置固定 |
| Base64 内联 | HTML 导出 | 单文件自包含，浏览器直接打开，无外部依赖 |
| 预签名 URL | 浏览器内预览（预留） | 避免 Base64 膨胀，大图场景更省流量；当前未接入端点 |

三种模式共用 `DocumentParseImageModel` 的 `ImagePath` / `ImageName` / `ImageType` 字段，通过 helper 方法隔离差异，端点层无需关心图片处理细节。

### HTML 转换方案

- 选用 **Markdig**（已在项目依赖中），`Markdown.ToHtml` 一步转换
- 包裹完整 HTML5 文档（`<!DOCTYPE html>` + `lang="zh-CN"` + 内联 CSS），确保浏览器直接打开即可阅读
- 内联 CSS 覆盖常用元素（标题/段落/列表/表格/引用/代码/图片），不引入外部样式库

### Markdown 导出为何用 ZIP 而非裸 Markdown

裸 Markdown 中图片为相对路径，单独分发 `.md` 时图片不可见。打包为 ZIP 后：
- `.md` 与 `images/` 目录一起分发，相对路径天然可用
- 用户解压即用，无需手动整理图片

### 为何 MarkdownExportHelper 设计为 internal static

- 导出逻辑是纯函数式变换（输入 string/流 → 输出 string/流），无状态、无外部依赖（OSS 通过参数传入）
- 不需要 DI 生命周期管理，避免无谓的 scoped/transient 分配
- 端点层负责 HTTP 校验与编排，helper 层负责可单元测试的纯逻辑

### OSS 下载失败容错

导出是 best-effort 场景：单张图片失败不应阻塞整份文档导出。因此：
- `BuildMarkdownZipAsync` / `ReplaceImagePathsBase64Async` / `ReplaceImagePathsPresignedAsync` 均在循环内 try/catch
- 失败仅记 `LogWarning`（含 `ImagePath`），继续处理下一张

## 错误处理

| 场景 | 处理 |
|------|------|
| 文件不存在 | 404 + `DOCLIBRARY_FILE_NOT_FOUND` |
| 解析记录不存在 | 404 + `DOCLIBRARY_PARSE_NOT_FOUND` |
| 文件未解析 | 422 + `DOCLIBRARY_FILE_NOT_PARSED` |
| 解析记录未解析 | 422 + `DOCLIBRARY_PARSE_NOT_PARSED` |
| 单张图片 OSS 下载失败 | 记 Warning 日志，跳过该张 |
| 单张图片预签名失败 | 记 Warning 日志，跳过该张 |

## 外部依赖

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentParseService` | 解析记录 / 图片列表查询 | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `IDocumentFileService` | 文件查询 | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `IOssService` | 文件上传下载 / 预签名 URL | `Ruoyu.Study.Common.Oss`（ruoyu.common） |
| `Markdig` | Markdown → HTML 转换 | NuGet 包 |

## DI 注册

```csharp
// Program.cs — 端点注册（无认证，内网管理后台）
app.MapDocumentExportEndpoints();
```

`MarkdownExportHelper` 为 `internal static`，无需 DI 注册。`IDocumentParseService` / `IDocumentFileService` / `IOssService` 已在 Program.cs 中注册，导出端点直接通过参数注入使用。
