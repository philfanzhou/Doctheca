# DocumentManagement — 任务列表

> **本模块所有任务已完成。** 以下为历史记录。

## 已完成任务

| ID | 任务 | 状态 |
|----|------|------|
| DF-01 | 创建 DocumentFileEntity（Database/Entities） | completed |
| DF-02 | 创建 DocumentFileModel（Domain/Models） | completed |
| DF-03 | 创建 IDocumentFileRepository 接口 | completed |
| DF-04 | 创建 IDocumentFileService 接口 | completed |
| DF-05 | 实现 DocumentFileRepository（CRUD + MapToEntity/MapToModel） | completed |
| DF-06 | 实现 DocumentFileService（Create/GetList/UpdateMetadata/Delete） | completed |
| DF-07 | 实现 DocumentFileEndpoints（6 个端点 + UpdateMetadataRequest DTO） | completed |
| DF-08 | 实现 MarkdownExportHelper.ReplaceImagePathsPresignedAsync | completed |
| DF-09 | DatabaseInitializer 原生 SQL 建表（CREATE TABLE IF NOT EXISTS） | completed |
| DF-10 | DI 注册（Program.cs AddScoped/AddSingleton） | completed |
| DF-11 | 单元测试 DocumentFileServiceTests（5 tests） | completed |
| DF-12 | 单元测试 DocumentFileDeleteCleanupTests（3 tests） | completed |

## 命令速查

```bash
# 构建
dotnet build Ruoyu.Study.DocLibrary.sln --configuration Release

# 运行本模块相关单元测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentFileService"
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentFileDeleteCleanup"

# 运行所有测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests
```
