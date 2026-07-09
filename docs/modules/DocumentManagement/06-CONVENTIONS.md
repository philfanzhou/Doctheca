# DocumentManagement — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 接口 | `I` 前缀 + PascalCase | `IDocumentFileService` |
| 实现类 | PascalCase，无后缀 | `DocumentFileService` |
| Record（DTO） | PascalCase | `UpdateMetadataRequest` |
| 常量 | PascalCase | `DocumentParseStatus.Pending` |
| 方法 | PascalCase，Async 后缀 | `CreateAsync`、`GetByIdAsync` |
| 参数 | camelCase | `fileService`、`modelVersion` |
| 端点方法 | PascalCase（private static） | `UploadDocumentFile`、`DeleteDocumentFile` |
| 私有常量 | PascalCase | `MaxFileSize`、`DocumentFileMimeTypes` |

## 日志约定

- 成功/信息：`LogInformation`，含 fileId/parseId
- 降级/可恢复：`LogWarning`，含异常对象
- 未预期错误：`LogError`，含异常对象

关键日志消息：
```
"Document file uploaded: {Id}, FileName={FileName}"
"Document file created: {Id}, FileName={FileName}"
"Document file not found for metadata update: {Id}"
"Document file metadata updated: {Id}, Subject={Subject}, Grade={Grade}, Year={Year}"
"Document file parse requested: {Id}, ParseId={ParseId}, ModelVersion={ModelVersion}"
"Document file {FileId} deleted but {Count} OSS paths remain: {Paths}"
"Failed to delete OSS path: {Path}"
"Failed to delete OpenSearch index for document file {FileId}"
"Failed to sync OpenSearch metadata for file {FileId}"
"Failed to parse metadata update request for file {FileId}"
```

## 错误消息约定

HTTP 响应消息使用中文（面向终端用户），但代码内部异常消息使用英文：
- `"Request must be multipart/form-data"` — 内部校验消息
- `"File cannot be empty"` — HTTP 响应消息
- `"File size exceeds 200MB limit"` — HTTP 响应消息
- `"Unsupported file format"` — HTTP 响应消息

### 错误码约定

| 错误码 | HTTP | 使用场景 |
|--------|------|---------|
| `DOCLIBRARY_FILE_REQUIRED` | 400 | 上传空文件 |
| `DOCLIBRARY_FILE_FORMAT_UNSUPPORTED` | 400 | 上传格式不支持 |
| `DOCLIBRARY_FILE_NOT_FOUND` | 404 | 文件 ID 不存在 |
| `DOCLIBRARY_INVALID_MODEL_VERSION` | 400 | modelVersion 不合法 |
| `DOCLIBRARY_PARSE_IN_PROGRESS` | 422 | 同 modelVersion 解析进行中 |
| `DOCLIBRARY_MINERU_NOT_CONFIGURED` | 503 | MinerU ApiToken 未配置 |

## 空值语义

| 值 | 语义 |
|----|------|
| `DocumentFileModel.CreatedBy = null` | 无应用层认证（内网管理后台） |
| `UpdateMetadataRequest.Subject = null` | 不修改该字段 |
| `UpdateMetadataRequest.Subject = ""` | 清空该字段（写入空串） |
| `DocumentFileModel.Subject = null` | 未设置学科元数据 |
| `DocumentParseModel.ZipPath = null` | 无 zip 产出（如失败/旧解析） |

## 解析状态约定

`DocumentParseStatus` 是静态常量类，`Status` 字段为 `string` 类型（非 enum）：

| 常量 | 值 | 含义 |
|------|---|------|
| `Pending` | `"pending"` | 等待解析 |
| `Parsing` | `"parsing"` | 解析中 |
| `Parsed` | `"parsed"` | 解析完成 |
| `Failed` | `"failed"` | 解析失败 |

## 文件上传约定

- MIME 白名单校验：仅接受 `application/pdf`、`application/msword`、`application/vnd.openxmlformats-officedocument.wordprocessingml.document`、`application/vnd.ms-powerpoint`、`application/vnd.openxmlformats-officedocument.presentationml.presentation`
- OSS object name 格式：`$"{Guid.NewGuid()}{ext}"`（ext 含前导点，默认 `.bin`）
- OSS bucket：`OssBucket.Documents`，路径前缀 `doclibrary-files/`
- `RequestSizeLimitAttribute(200 * 1024 * 1024)` + `FormOptions.MultipartBodyLengthLimit` 双重限制

## 测试约定

- 测试框架：xUnit + Moq + FluentAssertions
- 测试类命名：`{ServiceClass}Tests`
- 测试方法命名：`{Method}_{Scenario}_{ExpectedBehavior}`
- 使用 `Mock<ILogger<T>>` 验证日志行为时，通过 `Mock<ILoggerFactory>.Setup(f => f.CreateLogger(...))` 构造
