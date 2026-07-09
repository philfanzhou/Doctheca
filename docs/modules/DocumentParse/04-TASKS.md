# 04-TASKS — Document Parse 任务拆解

> **本模块所有任务已完成。** 以下为结构化任务清单，供后续维护与扩展参考。

## 阶段一：解析记录管理

| ID | 任务 | 代码落点 | 状态 |
|----|------|---------|------|
| DP-01 | `DocumentParseEntity`（含 `model_version` / `content_list_v2` / `model_json` / `layout_json`），移除 `layout_pdf_path` | `src/Database/Entities/DocumentParseEntity.cs`, `DatabaseInitializer.EnsureColumnsAsync` | completed |
| DP-02 | `DocumentParseBlockEntity` / `DocumentParseImageEntity` | `src/Database/Entities/` | completed |
| DP-03 | `IDocumentParseService` / `DocumentParseService`（CreateAsync/GetPendingJobsAsync/UpdateStatusAsync/DeleteParseAsync/GetListAsync） | `src/Domain/Services/` | completed |
| DP-04 | `IDocumentParseBlockService` / `DocumentParseBlockService`（content_list.json 解析 + 覆盖写入） | `src/Domain/Services/DocumentParseBlockService.cs` | completed |
| DP-05 | `DocumentParseRepository`（GetByStatusAsync / GetListAsync 排序 / GetLatestByFileIdAndModelAsync） | `src/Database/Repositories/DocumentParseRepository.cs` | completed |
| DP-06 | `DocumentParseEndpoints`（ListDocumentParses / DeleteDocumentParse） | `src/Service/Endpoints/DocumentParseEndpoints.cs` | completed |

## 阶段二：MinerU 解析管线

| ID | 任务 | 代码落点 | 状态 |
|----|------|---------|------|
| DP-07 | `MinerUPrecisionClient`（SubmitUrlAsync / PollStatusAsync / DownloadAndProcessZipAsync） | `src/Service/MinerUPrecisionClient.cs` | completed |
| DP-08 | `MinerUFileParseWorker`（ExecuteAsync 轮询 / ProcessFileAsync 流程编排） | `src/Service/MinerUFileParseWorker.cs` | completed |
| DP-09 | `PersistParseResultAsync`（ZIP 上传 / 图片入库 / blocks 写入 / 状态更新） | `src/Service/MinerUFileParseWorker.cs` | completed |
| DP-10 | 解析状态机（pending → parsing → parsed / failed）+ `ParsedAt` 自动设置 | `DocumentParseService.UpdateStatusAsync`, `MinerUFileParseWorker` | completed |
| DP-11 | 触发解析端点（校验链：文件存在 / 无进行中解析 / Token 配置） | `DocumentFileEndpoints.ParseDocumentFile` | completed |
| DP-12 | best-effort 后置：OpenSearch 索引 + LLM 元数据分析 | `MinerUFileParseWorker.IndexBlocksToSearchAsync` / `AnalyzeMetadataIfMissingAsync` | completed |

## 阶段三：转档与分块

| ID | 任务 | 代码落点 | 状态 |
|----|------|---------|------|
| DP-13 | `IFileConversionService` / `LibreOfficeConversionService`（DOC/PPT → PDF，60s 超时） | `src/Service/LibreOfficeConversionService.cs` | completed |
| DP-14 | `IPdfSplitService` / `PdfSplitService`（GetPageCount / SplitPdf，PdfSharpCore） | `src/Service/PdfSplitService.cs` | completed |
| DP-15 | 大文件分块解析（ProcessSplitFileAsync：分块上传 / 逐块提交 / 合并 / 清理临时文件） | `src/Service/MinerUFileParseWorker.cs` | completed |
| DP-16 | `PersistMergedChunkResultsAsync`（Markdown 拼接 / content_list 合并 / 图片名前缀 / 状态判定） | `src/Service/MinerUFileParseWorker.cs` | completed |

## 阶段四：持久化与清理

| ID | 任务 | 代码落点 | 状态 |
|----|------|---------|------|
| DP-17 | 删除解析记录联动（OSS 图片 → DB 级联 → OpenSearch 索引 best-effort） | `DocumentParseEndpoints.DeleteDocumentParse` | completed |
| DP-18 | ZIP 上传 OSS（mineru/{fileId}/mineru-output.zip） | `MinerUFileParseWorker.PersistParseResultAsync` | completed |
| DP-19 | 图片上传 OSS（mineru/{taskId}/{imageName}）+ Markdown 路径替换 | `MinerUPrecisionClient.DownloadAndProcessZipAsync` | completed |

## 命令速查

```bash
# 构建
dotnet build Ruoyu.Study.DocLibrary.sln --configuration Release

# 测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --configuration Release

# 本地运行（Local OSS，数据库为 PostgreSQL）
USE_LOCAL_OSS=1 dotnet run --project src/Host
```

## 后续扩展（未实现）

- Worker 多实例部署：`pending` 任务乐观锁 / 分布式锁
- 分块并行提交（当前串行）
- MinerU 解析失败自动重试（当前需人工重新触发）
- 解析进度实时推送（WebSocket / SSE）
