# 05-TESTS — Document Parse 测试文档

## 测试策略概览

本模块单元测试覆盖 **纯逻辑层**（block 解析、PDF 拆分、远程转换 HTTP 客户端、content_list 合并）。以下组件**无单元测试**，需集成测试或手动验证：

- `MinerUFileParseWorker`（依赖外部 MinerU API + OSS + OpenSearch，编排逻辑可通过 mock 测试）
- `MinerUPrecisionClient`（依赖外部 MinerU HTTP API）
- `DocumentParseEndpoints`（HTTP 集成，需端到端测试）

## 单元测试 (UT)

### DocumentParseBlockServiceTests.cs

覆盖 `IDocumentParseBlockService.InsertBlocksFromContentListAsync`。**FR-09 / AC-04**。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DP-01 | `InsertBlocksFromContentListAsync_WithValidJson_InsertsAllBlocks` | 合法 JSON 解析为 blocks（text / image / equation），image 关联正确 |
| UT-DP-02 | `InsertBlocksFromContentListAsync_AssignsSortIndexPerPage` | 每页 sort_index 独立从 0 递增 |
| UT-DP-03 | `InsertBlocksFromContentListAsync_WithoutImageMap_LeavesImageIdNull` | 无 imageMap 时 image 类型 block 的 ImageId 为 null |
| UT-DP-04 | `InsertBlocksFromContentListAsync_PreservesRawBlockData` | `block_data` 保留原始 JSON（含未知字段，兜底） |
| UT-DP-05 | `InsertBlocksFromContentListAsync_WithEmptyJson_DoesNotCallRepo` | `"[]"` 不调用仓储 |
| UT-DP-06 | `InsertBlocksFromContentListAsync_WithEmptyString_DoesNotCallRepo` | 空字符串不调用仓储 |
| UT-DP-07 | `InsertBlocksFromContentListAsync_WithInvalidJson_DoesNotCallRepo` | 非法 JSON 不调用仓储 |
| UT-DP-08 | `InsertBlocksFromContentListAsync_DefaultsPageIdToZero_WhenMissing` | 缺 `page_id` 时默认 0 |
| UT-DP-09 | `InsertBlocksFromContentListAsync_ExtractsTextFromContentOrBodyField` | 文本提取优先级 `text` → `content` → `body`（table HTML） |

### PdfSplitServiceTests.cs

覆盖 `IPdfSplitService.GetPageCount` / `SplitPdf`，以及 Markdown 分隔符拼接、图片名前缀冲突避免的纯逻辑。**FR-05 / AC-06**。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DP-10 | `GetPageCount_ReturnsCorrectCount` | 5 页 PDF 返回 5 |
| UT-DP-11 | `GetPageCount_SinglePage_Returns1` | 单页 PDF 返回 1 |
| UT-DP-12 | `SplitPdf_SmallFile_ReturnsSingleChunk` | 3 页（<200）返回单块，chunkIndex=0 |
| UT-DP-13 | `SplitPdf_ExactMultiple_ReturnsCorrectChunks` | 6 页 / 每块 3 页 → 2 块 |
| UT-DP-14 | `SplitPdf_RemainderPages_ReturnsCorrectChunks` | 7 页 / 每块 3 页 → 3 块 |
| UT-DP-15 | `SplitPdf_ChunksHaveCorrectPageCounts` | 各块页数正确（3 / 3 / 1） |
| UT-DP-16 | `SplitPdf_SinglePageFile_ReturnsSingleChunk` | 单页文件返回单块 |
| UT-DP-17 | `MergeMarkdown_JoinsWithSeparator` | Markdown 以 `---` 分隔拼接 |
| UT-DP-18 | `ImageNamePrefix_AvoidsCollision` | `chunk{i}_` 前缀避免跨块图片名冲突 |

### FileConversionServiceTests.cs

覆盖 `RemoteFileConversionService`（HTTP 调用 doc-converter）的 mock 测试。**FR-04 / AC-05**。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DP-19 | `IsAvailable_AlwaysReturnsTrue` | HTTP 服务可用性始终返回 true（懒检查） |
| UT-DP-20 | `ConvertToPdfAsync_ReturnsStream_OnSuccess` | 成功转换返回 PDF 流 |
| UT-DP-21 | `ConvertToPdfAsync_ReturnsNull_OnNonSuccessStatusCode` | HTTP 非 2xx 返回 null |
| UT-DP-22 | `ConvertToPdfAsync_ReturnsNull_OnHttpRequestException` | 网络异常返回 null |
| UT-DP-23 | `ConvertToPdfAsync_SeeksStreamToStartBeforeReading` | 调用前 reset stream position |
| UT-DP-24 | `Constructor_SetsBaseUrlAndTimeout` | 构造时正确设置 BaseAddress 和 Timeout |
| UT-DP-25 | `ConvertToPdfAsync_SendsMultipartFormData` | 请求为 multipart/form-data |
| UT-DP-26 | `IsPdfFile_DetectedByContentType` | ContentType `application/pdf` 识别 |
| UT-DP-27 | `IsPdfFile_DetectedByExtension` | 扩展名 `.pdf` 识别 |

### MinerUFileParseWorkerTests.cs

覆盖 `MinerUFileParseWorker` 的 content_list 合并逻辑和编排流程（mock 依赖）。**FR-05 / AC-06**。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DP-28 | `MergeContentListArrays_EmptyList_ReturnsEmptyArray` | 空列表返回 `[]` |
| UT-DP-29 | `MergeContentListArrays_SingleChunk_ReturnsSameArray` | 单 chunk 原样返回 |
| UT-DP-30 | `MergeContentListArrays_MultipleChunks_MergesAllBlocks` | 多 chunk 合并所有 block |
| UT-DP-31 | `MergeContentListArrays_SkipsMalformedJson` | 跳过非法 JSON 的 chunk |

## 集成测试（规划）

| # | 测试用例 | 覆盖 | 端点 |
|---|---------|------|------|
| IT-DP-01 | 触发解析 → Worker 完成 → status=parsed | AC-01, AC-04 | `POST /admin/document-files/{id}/parse` |
| IT-DP-02 | 同文件同 modelVersion 重复触发 → 422 | AC-02 | `POST /admin/document-files/{id}/parse` |
| IT-DP-03 | Token 未配置 → 503 | AC-03 | `POST /admin/document-files/{id}/parse` |
| IT-DP-04 | 解析记录列表分页 + search 过滤 | AC-08 | `GET /admin/document-parses` |
| IT-DP-05 | 删除解析记录 → OSS 图片 / DB / OpenSearch 清理 | AC-09 | `DELETE /admin/document-parses/{parseId}` |
| IT-DP-06 | 非 PDF 文件自动转 PDF 后解析 | AC-05 | `POST /admin/document-files/{id}/parse` |
| IT-DP-07 | >200 页 PDF 自动分块解析 | AC-06 | `POST /admin/document-files/{id}/parse` |
| IT-DP-08 | MinerU 失败 → status=failed | AC-07 | `POST /admin/document-files/{id}/parse` |
| IT-DP-09 | 同一文件 vlm + pipeline 两条解析并存 | AC-11 | `POST /admin/document-files/{id}/parse` |

## 其他相关测试文件（属于其他模块，供参考）

以下测试文件存在于测试项目中，但**不属于本模块**：

| 测试文件 | 所属模块 |
|---------|---------|
| `DocumentFileServiceTests.cs` | DocumentManagement |
| `DocumentFileDeleteCleanupTests.cs` | DocumentManagement |
| `DocumentExportLogicTests.cs` | DocumentExport |
| `OpenSearchIndexServiceTests.cs` | DocumentSearch |
| `SearchDomainServiceTests.cs` | DocumentSearch |
| `QuestionBankImportServiceTests.cs` | QuestionBankImport |
| `ConstantsTests.cs` | 共享 |
| `SensitiveDataMaskerTests.cs` | 共享 |
