# DocumentMetadataAnalysis — 任务列表

> **本模块实现任务已完成,但单元测试尚未编写。** 详见 05-TESTS。

## 已完成任务

| ID | 任务 | 状态 |
|----|------|------|
| DM-01 | 创建 DocumentMetadataAnalysis record (Domain/Models) | completed |
| DM-02 | 创建 DocumentAnalysisOptions (继承 AiClientOptions) | completed |
| DM-03 | 实现 DocumentAnalysisService.AnalyzeMetadataAsync | completed |
| DM-04 | 创建 IDocumentAnalysisService 接口 | completed |
| DM-05 | 创建 DocLibraryConstants（ValidSubjects / ValidGrades） | completed |
| DM-06 | DocumentFileEntity 新增 Subject/Grade/Year 列 | completed |
| DM-07 | DocumentFileModel 新增 Subject/Grade/Year 字段 | completed |
| DM-08 | IDocumentFileRepository 新增 UpdateMetadataAsync | completed |
| DM-09 | IDocumentFileService 新增 UpdateMetadataAsync | completed |
| DM-10 | IDocumentFileRepository.UpdateMetadataAsync 实现 | completed |
| DM-11 | DocumentFileService.UpdateMetadataAsync 实现 | completed |
| DM-12 | ISearchIndexService 新增 UpdateDocumentFileMetadataAsync | completed |
| DM-13 | OpenSearchIndexService.UpdateDocumentFileMetadataAsync 实现 | completed |
| DM-14 | DocumentFileEndpoints 新增 PUT /{id}/metadata | completed |
| DM-15 | MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync 实现 | completed |
| DM-16 | DI 注册 IDocumentAnalysisService（条件注册） | completed |
| DM-17 | Program.cs LLM 初始化调用 InitializeAsync | completed |

## 待完成任务

| ID | 任务 | 状态 | 说明 |
|----|------|------|------|
| DM-18 | 单元测试 DocumentAnalysisService(覆盖内部方法) | pending | 02-SPEC §7.2 建议方向,当前零测试覆盖 |

## 命令速查

```bash
# 构建
dotnet build Ruoyu.Study.DocLibrary.sln --configuration Release

# 测试(目前无 DocumentAnalysis 测试可筛选)
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --configuration Release

# 前端构建
cd frontend && npm run build
```
