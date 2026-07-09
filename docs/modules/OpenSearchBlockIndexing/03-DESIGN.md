# OpenSearchBlockIndexing — 设计说明

## 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Service/
│   │   ├── OpenSearchIndexService.cs           # OpenSearch 索引实现
│   │   └── MinerUFileParseWorker.cs            # 解析完成后调用 IndexParseBlocksAsync
│   ├── Domain/
│   │   ├── Repositories/
│   │   │   ├── ISearchIndexService.cs           # 搜索索引接口（6 个方法）
│   │   │   └── IDocumentParseBlockRepository.cs # block 仓储接口
│   │   └── Models/
│   │       ├── SearchResultModel.cs             # 搜索结果模型
│   │       └── SearchFilterModel.cs             # 搜索过滤模型
│   └── Database/
│       └── Repositories/
│           └── DocumentParseBlockRepository.cs  # block 仓储实现
├── src/Service/Endpoints/
│   ├── DocumentParseEndpoints.cs                # DELETE 时清理索引
│   └── DocumentFileEndpoints.cs                 # DELETE 时清理索引
└── docs/modules/OpenSearchBlockIndexing/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (本文档)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 接口设计

### ISearchIndexService 新增方法

| 方法 | 签名 | 调用时机 |
|------|------|---------|
| `IndexParseBlocksAsync` | `Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year)` | MinerU 解析完成后 |
| `DeleteParseIndexAsync` | `Task DeleteParseIndexAsync(Guid parseId)` | DELETE /admin/document-parses/{parseId} |
| `DeleteDocumentFileIndexAsync` | `Task DeleteDocumentFileIndexAsync(Guid documentFileId)` | DELETE /admin/document-files/{id} |
| `UpdateDocumentFileMetadataAsync` | `Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year)` | 元数据更新后（手动或 LLM） |

### OpenSearch Mapping

| 字段 | 类型 | 说明 |
|------|------|------|
| `parse_id` | keyword | 按 parse 删除 |
| `document_file_id` | keyword | 按文档删除 |
| `file_name` | keyword | 搜索结果展示 |
| `subject` / `grade` / `year` | keyword | 元数据过滤 |
| `page_number` | integer | 页码 |
| `block_id` | keyword | 唯一标识 |
| `block_type` | keyword | 版面块类型 |
| `text` | text (english_custom) | 索引内容 |
| `sort_index` | integer | 块顺序 |
| `image_id` | keyword | 图片 ID |
| `created_at` | date | 创建时间 |

`text` 字段有两个子字段：
- `text.exact` — english_phrase 分析器（仅 lowercase，保留短语完整性）
- `text.keyword` — keyword 类型（ignore_above=256）

## 数据流

### 索引流程（IndexParseBlocksAsync）

```
1. 从 IDocumentParseBlockRepository.GetByParseIdAsync(parseId) 读取所有 blocks
2. 过滤 text_content 为空的 block
3. 构建 bulk 请求（每 block 2 行：index 指令 + 文档）
   _id = "block_{blockId}"
4. 调用 _client.BulkAsync 提交
5. 记录日志（parseId, documentFileId, blockCount）
```

### 删除流程（DeleteParseIndexAsync / DeleteDocumentFileIndexAsync）

```
1. 构建 delete_by_query 请求（按 parse_id 或 document_file_id 匹配）
2. 调用 _client.DeleteByQueryAsync
3. 记录日志（parseId/documentFileId, deletedCount）
```

### 元数据同步流程（UpdateDocumentFileMetadataAsync）

```
1. 构建 update_by_query 请求（按 document_file_id 匹配）
2. script 更新 subject/grade/year 字段
3. 调用 _client.UpdateByQueryAsync
4. 记录日志
```

## 调用点

| 调用位置 | 方法 | 时机 |
|----------|------|------|
| `MinerUFileParseWorker.PersistParseResultAsync` | `IndexParseBlocksAsync` | 解析状态变为 parsed 后 |
| `MinerUFileParseWorker.PersistMergedChunkResultsAsync` | `IndexParseBlocksAsync` | 合并后状态变为 parsed |
| `DocumentParseEndpoints.DeleteDocumentParse` | `DeleteParseIndexAsync` | 删除解析记录后 |
| `DocumentFileEndpoints.DeleteDocumentFile` | `DeleteDocumentFileIndexAsync` | 删除文件后 |
| `DocumentFileEndpoints.UpdateDocumentFileMetadata` | `UpdateDocumentFileMetadataAsync` | 手动更新元数据后 |
| `MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync` | `UpdateDocumentFileMetadataAsync` | LLM 分析完成后 |

所有 OpenSearch 调用均为 best-effort：失败时记录 Warning 日志，不阻塞主流程。

## 错误处理策略

| 场景 | 处理 |
|------|------|
| blocks 为空 | 记 Warning 日志，返回（不抛异常） |
| OpenSearch 连接失败 | 抛异常（调用方捕获后记 Warning） |
| Bulk 部分失败 | 记 Warning 日志（含失败数），不抛异常 |
| text_content 为 null | 跳过该 block |
| delete_by_query 无匹配 | 记 Info 日志 |
| update_by_query 失败 | 记 Warning 日志 |

## 搜索查询流程（ExactSearch）

详见 [ExactSearch 模块](../ExactSearch/03-DESIGN.md)。

`OpenSearchIndexService.ExactSearchAsync` 使用本模块建立的索引：
- `phrase=true`：`match_phrase` 查询 `text.exact` 字段
- `phrase=false`：`match` 查询 `text` 字段
- 过滤：subject/grade/year/document_title 的 term 过滤
- 分页：`search_after` 游标
- 排序：`_score desc, block_id asc`

## 外部依赖

| 依赖 | 类型 | 说明 |
|------|------|------|
| OpenSearch 2.19 | 外部服务 | 索引存储和搜索 |
| OpenSearch.Net 1.8 | NuGet | .NET 客户端 |
