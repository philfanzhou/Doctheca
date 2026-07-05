# 02-SPEC — OpenSearch Block Indexing 详细规格

## 1. 接口变更

### 1.1 ISearchIndexService 新增方法

```csharp
/// <summary>
/// Index all blocks of a parse to OpenSearch. Called after MinerU parse completes.
/// Idempotent: re-indexing overwrites existing documents (same _id = block_{blockId}).
/// </summary>
Task IndexParseBlocksAsync(Guid parseId, Guid documentFileId, string fileName, string? subject, string? grade, string? year);

/// <summary>
/// Delete all OpenSearch documents for a parse. Called when parse result is deleted.
/// </summary>
Task DeleteParseIndexAsync(Guid parseId);

/// <summary>
/// Delete all OpenSearch documents for a document file (across all parses).
/// Called when document file is deleted.
/// </summary>
Task DeleteDocumentFileIndexAsync(Guid documentFileId);
```

### 1.2 保留的方法

| 方法 | 说明 |
|------|------|
| `EnsureIndexAsync` | 启动时调用，创建/更新 mapping |
| `ExactSearchAsync` | 搜索逻辑，基于 blocks 索引字段 |

## 2. OpenSearch Mapping

### 2.1 索引字段

```json
{
  "parse_id": { "type": "keyword" },
  "document_file_id": { "type": "keyword" },
  "file_name": { "type": "keyword" },
  "block_id": { "type": "keyword" },
  "block_type": { "type": "keyword" },
  "sort_index": { "type": "integer" },
  "image_id": { "type": "keyword" },
  "subject": { "type": "keyword" },
  "grade": { "type": "keyword" },
  "year": { "type": "keyword" },
  "page_number": { "type": "integer" },
  "text": {
    "type": "text",
    "analyzer": "english_custom",
    "fields": {
      "exact": { "type": "text", "analyzer": "english_phrase" },
      "keyword": { "type": "keyword", "ignore_above": 256 }
    }
  },
  "created_at": { "type": "date" }
}
```

### 2.2 索引策略

- OpenSearch mapping 是动态的，`EnsureIndexAsync` 创建索引时使用上述 mapping
- 索引数据来源为 `document_parse_blocks` 表（MinerU 解析链路）
- 搜索时 `ParseSearchResponse` 直接读取上述字段

## 3. IndexParseBlocksAsync 实现规格

### 3.1 输入

| 参数 | 类型 | 说明 |
|------|------|------|
| `parseId` | Guid | 解析记录 ID |
| `documentFileId` | Guid | 文档文件 ID |
| `fileName` | string | 文件名(搜索结果展示) |
| `subject` | string? | 学科(当前留空) |
| `grade` | string? | 年级(当前留空) |
| `year` | string? | 年份(当前留空) |

### 3.2 数据流

```
1. 从 IDocumentParseBlockRepository.GetByParseIdAsync(parseId) 读取 blocks
2. 若 blocks 为空,记 Warning 日志,返回
3. 构建 bulk 索引请求:
   - 每个block生成2行: { index: { _index, _id: "block_{blockId}" } } + 文档
   - 文档字段: parse_id, document_file_id, file_name, subject, grade, year,
              page_number (= block.PageId), block_id, block_type, text (= block.TextContent),
              sort_index, image_id, created_at
4. 调用 _client.BulkAsync 提交
5. 记日志: parseId, documentFileId, blockCount, success/failure
```

### 3.3 错误处理

| 场景 | 处理 |
|------|------|
| blocks 为空 | 记 Warning 日志,返回(不抛异常) |
| OpenSearch 连接失败 | 抛异常(调用方捕获后记 Warning 日志,不阻塞解析) |
| Bulk 部分失败 | 记 Warning 日志(含失败数),不抛异常 |
| text_content 为 null | 跳过该 block(不索引空文本) |

## 4. DeleteParseIndexAsync 实现规格

### 4.1 输入

| 参数 | 类型 | 说明 |
|------|------|------|
| `parseId` | Guid | 要删除的解析记录 ID |

### 4.2 数据流

```
1. 构建 delete_by_query 请求:
   { query: { term: { parse_id: parseId.ToString() } } }
2. 调用 _client.DeleteByQueryAsync
3. 记日志: parseId, deletedCount, success/failure
```

### 4.3 错误处理

| 场景 | 处理 |
|------|------|
| OpenSearch 连接失败 | 抛异常(调用方捕获后记 Warning 日志,不阻塞删除) |
| 无匹配文档 | 记 Info 日志(正常情况) |

## 5. DeleteDocumentFileIndexAsync 实现规格

### 5.1 输入

| 参数 | 类型 | 说明 |
|------|------|------|
| `documentFileId` | Guid | 要删除的文档文件 ID |

### 5.2 数据流

```
1. 构建 delete_by_query 请求:
   { query: { term: { document_file_id: documentFileId.ToString() } } }
2. 调用 _client.DeleteByQueryAsync
3. 记日志: documentFileId, deletedCount, success/failure
```

### 5.3 错误处理

同 DeleteParseIndexAsync。

## 6. ParseSearchResponse 字段映射

### 6.1 字段读取

| 响应字段 | 读取字段 |
|---------|----------|
| `DocumentName` | `file_name` |
| `PageNumber` | `page_number` |
| `SegmentId` | `block_id` |
| `MatchType` | phrase ? `exact_phrase` : `stemmed` |
| `AssociatedText` | `text`(高亮优先) |
| `StartOffset` | 0（blocks 无 offset） |
| `EndOffset` | 0（blocks 无 offset） |
| `Score` | `_score` |
| `CreatedAt` | `created_at` |

### 6.2 block_type 取值

- `block_type` 取值：text/image/table/equation/list/code
- 搜索响应 `matchType` 不受 `block_type` 影响（由 `phrase` 参数决定）

## 7. MinerUFileParseWorker 变更

### 7.1 PersistParseResultAsync 变更

在 `parseService.UpdateStatusAsync(DocumentParseStatus.Parsed, ...)` 之后,新增:

```csharp
// Index blocks to OpenSearch (best-effort, failure does not block parse)
try
{
    var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
    await searchIndexService.IndexParseBlocksAsync(parse.Id, file.Id, file.FileName, null, null, null);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to index parse {ParseId} to OpenSearch", parse.Id);
}
```

### 7.2 PersistMergedChunkResultsAsync 变更

同 7.1,在 `parseService.UpdateStatusAsync(status, ...)` 之后新增相同逻辑。

### 7.3 失败状态不索引

- 若 `status == DocumentParseStatus.Failed`,不触发索引(仅 Parsed 才索引)
- 实现:在调用 `IndexParseBlocksAsync` 前判断 `status == DocumentParseStatus.Parsed`

## 8. DocumentParseEndpoints.DeleteDocumentParse 变更

在 `parseService.DeleteParseAsync(parseId)` 之后,新增:

```csharp
// Delete OpenSearch index for this parse (best-effort)
try
{
    var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();
    await searchIndexService.DeleteParseIndexAsync(parseId);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to delete OpenSearch index for parse {ParseId}", parseId);
}
```

注意:`DeleteDocumentParse` 当前是静态方法,需注入 `IServiceProvider` 或改为注入 `ISearchIndexService`。

## 9. DocumentFileEndpoints.DeleteDocumentFile 变更

在 `fileService.DeleteAsync(id)` 之后,新增:

```csharp
// Delete OpenSearch index for this document file (best-effort)
try
{
    var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();
    await searchIndexService.DeleteDocumentFileIndexAsync(id);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to delete OpenSearch index for document file {FileId}", id);
}
```

注意:同 8,需注入 `IServiceProvider` 或 `ISearchIndexService`。

## 10. 错误码

本模块不新增错误码,所有失败均为 best-effort(仅记日志,不返回错误)。

## 11. 测试策略

### 11.1 单元测试(UT)

纯逻辑测试(`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 静态方法),不依赖 OpenSearch HTTP 调用。已实现于 `OpenSearchIndexServiceTests.cs`。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-OBI-08a | `ParseSearchResponse_WithBlockFields_ReadsBlockIdAndFileName` — 读取 `block_id` / `file_name` | FR-06 / FR-07 / AC-06 |
| UT-OBI-08b | `ParseSearchResponse_WithBlockFields_PhraseSetsExactPhraseMatchType` — phrase 查询返回 `ExactPhrase` | FR-06 / AC-06 |
| UT-OBI-10a | `BuildIndexBody_IncludesBlockPipelineFields` — 索引字段(`parse_id`/`document_file_id`/`file_name`/`block_id`/`block_type`/`sort_index`/`image_id`)在 mapping 中且类型正确 | FR-05 |
| UT-OBI-10c | `BuildIndexBody_RetainsSharedFieldsAndAnalyzers` — 共享字段和分析器(`english_custom`/`english_stemmer`/`english_stop`/`english_phrase`/`exact`)保留 | FR-05 / FR-07 |

### 11.2 单元测试覆盖说明

UT-OBI-01~07(原规划 `IndexParseBlocksAsync` / `DeleteParseIndexAsync` / `DeleteDocumentFileIndexAsync` 直接测试)依赖 `OpenSearchLowLevelClient` 真实 HTTP 调用与 EF Core 仓储,属集成测试范畴,移至 11.3。本服务沿用现有测试约定:仅对纯逻辑静态方法做单元测试,依赖外部 IO 的方法通过集成测试覆盖。

为支持 `BuildIndexBody` 单元测试,该方法可见性由 `private static` 调整为 `internal static`(本程序集已通过 `InternalsVisibleTo` 暴露给测试程序集)。

### 11.3 集成测试(规划)

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-OBI-01 | MinerU 解析完成后 OpenSearch 有 blocks 数据(原 UT-OBI-01/02/03/04) | AC-01 / FR-01 |
| IT-OBI-02 | 同一文档多次解析,OpenSearch 有多份 blocks(幂等覆盖) | AC-02 / FR-02 |
| IT-OBI-03 | 删除 parse 后 OpenSearch 无该 parse 数据(原 UT-OBI-05) | AC-03 / FR-03 |
| IT-OBI-04 | 删除文档后 OpenSearch 无该文档数据(原 UT-OBI-06) | AC-04 / FR-04 |
| IT-OBI-05 | 搜索能命中 blocks 数据 | AC-05 |
| IT-OBI-06 | 搜索响应字段兼容前端 | AC-06 |
| IT-OBI-07 | OpenSearch 不可用时索引/删除 best-effort(原 UT-OBI-07) | NFR-04 |

## 12. 影响范围

### 12.1 新增文件

无(仅修改现有文件)

### 12.2 修改文件

| 文件 | 修改 |
|------|------|
| `ISearchIndexService.cs` | 新增 3 个方法签名 |
| `OpenSearchIndexService.cs` | 实现新方法;`BuildIndexBody` 索引字段;`ParseSearchResponse` 读取 blocks 字段 |
| `MinerUFileParseWorker.cs` | `PersistParseResultAsync` / `PersistMergedChunkResultsAsync` 新增索引调用 |
| `DocumentParseEndpoints.cs` | `DeleteDocumentParse` 新增索引删除 |
| `DocumentFileEndpoints.cs` | `DeleteDocumentFile` 新增索引删除 |

### 12.3 不受影响

- `SearchDomainService.cs` — 搜索逻辑不变(OpenSearch 检索，不可用时返回空结果)
- `DocumentSearchEndpoints.cs` — 响应字段不变
- 前端 `SearchPage.vue` — 无需改动
- QuestionBank 拉模式 — 无影响(基于 blocks 表,与索引独立)
