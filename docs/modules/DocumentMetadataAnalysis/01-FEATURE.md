# 01-FEATURE — Document Metadata Analysis

## 功能概述

在解析完成后，自动调用 LLM 分析文档前 2000 字符的 Markdown 内容，识别学科（subject）和年级（grade），并将结果回填到 `document_files` 表。如果文档的 subject/grade/year 已全部有值（人工填写或之前 LLM 分析过），则跳过 LLM 调用。LLM 失败不影响解析流程。

## 背景

`document_files` 表当前没有 subject/grade/year 元数据字段。OpenSearch 索引时这些字段留空，导致无法按学科/年级过滤搜索。本功能：

1. 为 `document_files` 表新增 subject/grade/year 列（nullable）
2. 提供手动设置元数据的 API 端点
3. 在解析完成后自动调用 LLM 填充缺失的元数据
4. 元数据更新后同步刷新 OpenSearch 索引

## 用户故事

- **作为老师**:我上传讲义后，系统自动识别学科和年级，无需手动填写
- **作为老师**:我可以在解析前或解析后手动指定学科/年级，系统不会覆盖我的选择
- **作为运维**:LLM 不可用时，解析流程照常完成，只是元数据留空
- **作为运维**:文档多次解析时，如果元数据已有值，不会重复调用 LLM

## 功能需求

### FR-01:document_files 新增元数据字段
- 新增 `subject`(string, nullable, max 50)、`grade`(string, nullable, max 20)、`year`(string, nullable, max 10)三列
- 上传时这些字段留空（上传接口不传元数据）
- 通过新的 PUT 端点手动设置

### FR-02:手动设置元数据端点
- `PUT /admin/document-files/{id}/metadata`
- 请求体:`{ "subject": "...", "grade": "...", "year": "..." }`(三个字段均可选，null 表示不修改)
- 响应:更新后的文档信息
- 手动设置后，后续解析完成时跳过 LLM 分析（只要三个字段都有值）

### FR-03:解析完成后自动 LLM 分析
- 解析状态变为 `parsed` 后，检查 `document_files` 的 subject/grade/year
- 若三个字段全部有值 → 跳过 LLM
- 若任一字段为空 → 调用 LLM 分析 Markdown 前 2000 字符
- LLM 返回后，仅更新为空的字段（不覆盖已有值）
- 同步更新 OpenSearch 索引中该 document_file 的所有 blocks 的 subject/grade/year

### FR-04:LLM 分析输入
- 输入:解析结果 `MarkdownContent` 的前 2000 字符（人类可读的 Markdown 正文，非 JSON）
- 提示词聚焦学科和年级识别（不要求分段策略、文档类型等）
- LLM 返回 JSON:`{ "subject": "...", "grade": "...", "year": "..." }`

### FR-05:LLM 失败不阻塞
- LLM 服务未配置（ApiKey 为空）→ 跳过，记 Info 日志
- LLM 调用失败（网络/超时/解析错误）→ 跳过，记 Warning 日志
- 解析状态不受影响（仍为 `parsed`）
- OpenSearch 索引不受影响（blocks 仍已索引，只是 subject/grade/year 为空）

### FR-06:OpenSearch 索引同步
- 元数据更新后（LLM 自动或手动），调用 `UpdateDocumentFileMetadataAsync` 刷新 OpenSearch
- 按 `document_file_id` 匹配，更新所有 blocks 的 subject/grade/year 字段
- 失败仅记 Warning 日志，不阻塞

## 验收条件

| AC | 描述 |
|----|------|
| AC-01 | `document_files` 表有 subject/grade/year 列，可为 null |
| AC-02 | `PUT /admin/document-files/{id}/metadata` 能更新元数据 |
| AC-03 | 解析完成后，若元数据全有值，不调用 LLM |
| AC-04 | 解析完成后，若元数据部分缺失，调用 LLM 填充缺失字段 |
| AC-05 | LLM 失败不影响解析状态和 OpenSearch 索引 |
| AC-06 | LLM 未配置时跳过分析，不报错 |
| AC-07 | 元数据更新后 OpenSearch 索引同步刷新 |
| AC-08 | 手动设置的元数据不会被后续 LLM 分析覆盖 |

## 非功能需求

| NFR | 描述 |
|-----|------|
| NFR-01 | LLM 分析异步执行，不阻塞解析状态更新 |
| NFR-02 | LLM 分析失败仅记日志，不影响任何后续流程 |
| NFR-03 | LLM 调用使用现有 `LlmDocumentAnalysis` 配置（BaseUrl/ApiKey/Model） |
| NFR-04 | LLM 输入为人类可读 Markdown（非 JSON），前 2000 字符 |
| NFR-05 | 日志记录 fileId/parseId/分析结果/失败原因，ApiKey 脱敏 |

## 数据来源

- **LLM 输入**:`document_parses.markdown_content` 前 2000 字符
- **元数据存储**:`document_files.subject` / `grade` / `year`
- **LLM 配置**:`LlmDocumentAnalysis` 配置段（复用现有配置，不新增）

## 接口清单

| 组件 | 修改 |
|------|------|
| `DocumentFileEntity` | 新增 Subject/Grade/Year 列 |
| `DocumentFileModel` | 新增 Subject/Grade/Year 字段 |
| `IDocumentFileRepository` | 新增 `UpdateMetadataAsync` |
| `IDocumentFileService` | 新增 `UpdateMetadataAsync` |
| `IDocumentAnalysisService` | 新增 `AnalyzeMetadataAsync` 方法 |
| `ISearchIndexService` | 新增 `UpdateDocumentFileMetadataAsync` 方法 |
| `StructaDocParseWorker` | 解析完成后调用 LLM 分析元数据 |
| `DocumentFileEndpoints` | 新增 `PUT /{id}/metadata` 端点 |
