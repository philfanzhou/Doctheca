# DocumentMetadataAnalysis — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 接口 | `I` 前缀 + PascalCase | `IDocumentAnalysisService` |
| 实现类 | PascalCase，无后缀 | `DocumentAnalysisService` |
| Record（DTO） | PascalCase | `DocumentMetadataAnalysis` |
| 常量 | PascalCase | `SubjectEnglish` |
| 方法 | PascalCase，Async 后缀 | `AnalyzeMetadataAsync` |
| 参数 | camelCase | `textPreview` |

## 日志约定

- 成功/信息：`LogInformation`，含 fileId/parseId
- 降级/可恢复：`LogWarning`，含异常对象
- 未预期错误：`LogError`，含异常对象
- LLM 密钥脱敏：使用 `SensitiveDataMasker.MaskApiKey()`

关键日志消息：
```
"Metadata already set for file {FileId}, skip LLM analysis"
"LLM not configured, skip metadata analysis for file {FileId}"
"No markdown content for file {FileId}, skip metadata analysis"
"Starting LLM metadata analysis for file {FileId}"
"Metadata updated by LLM for file {FileId}: Subject={Subject}, Grade={Grade}, Year={Year}"
"Failed to sync OpenSearch metadata for file {FileId}"
```

## 错误消息约定

错误消息使用中文（面向终端用户），但代码内部异常消息使用英文：
- `"LLM metadata analysis failed"` — 内部异常消息
- HTTP 响应消息由端点层控制

## 空值语义

| 值 | 语义 |
|----|------|
| `null` | 不修改（UpdateMetadataAsync 参数） |
| `""`（空字符串） | 清空字段 |
| `DocumentMetadataAnalysis` 字段为 null | LLM 无法确定该字段 |

## LLM 配置约定

- 配置节：`LlmDocumentAnalysis`
- 温度固定为 0.1（结构化输出）
- 流式 SSE 调用，stream idle timeout 默认 60s
- ContextLength 支持 `128K` / `1M` 等人类友好格式
