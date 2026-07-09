# MinerUAgentParsing — 任务列表

> **本模块所有任务已完成。** 以下为历史记录。

## 已完成任务

| ID | 任务 | 状态 |
|----|------|------|
| MAP-01 | DocumentFileEntity（含 subject/grade/year） | completed |
| MAP-02 | DocumentParseEntity（含 model_version / content_list_v2 / model_json / layout_json） | completed |
| MAP-03 | DocumentParseImageEntity | completed |
| MAP-04 | DocumentFileService CRUD | completed |
| MAP-05 | DocumentParseService CRUD + 状态管理 | completed |
| MAP-06 | DocumentFileEndpoints（上传/列表/详情/删除/元数据） | completed |
| MAP-07 | 前端文件管理页面（DocManagePage.vue） | completed |

## 阶段二：MinerU 解析管线

| ID | 任务 | 状态 |
|----|------|------|
| MAP-08 | MinerUPrecisionClient（Submit/Poll/Download） | completed |
| MAP-09 | MinerUFileParseWorker（轮询 + 流程编排） | completed |
| MAP-10 | DocumentParseEndpoints（列表/删除） | completed |
| MAP-11 | 解析状态流转（pending → parsing → parsed / failed） | completed |
| MAP-12 | 非 PDF 文件自动转换（LibreOfficeConversionService） | completed |
| MAP-13 | 大文档拆分（PdfSplitService） | completed |
| MAP-14 | 拆分结果合并（Markdown / content_list / 图片） | completed |

## 阶段三：持久化与索引

| ID | 任务 | 状态 |
|----|------|------|
| MAP-15 | DocumentParseBlockService（content_list.json → block rows） | completed |
| MAP-16 | OpenSearchIndexService 索引 blocks | completed |
| MAP-17 | OpenSearch 索引删除（parse/file 级别） | completed |
| MAP-18 | DocumentAnalysisService LLM 元数据 | completed |
| MAP-19 | MinerUFileParseWorker 调用 LLM 分析 | completed |
| MAP-20 | DocumentExportEndpoints（MD ZIP / HTML） | completed |

## 命令速查

```bash
# 构建
dotnet build Ruoyu.Study.DocLibrary.sln --configuration Release

# 测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests

# 前端构建
cd frontend && npm run build

# 本地运行（SQLite + Local OSS）
USE_LOCAL_OSS=1 dotnet run --project src/Host
```
