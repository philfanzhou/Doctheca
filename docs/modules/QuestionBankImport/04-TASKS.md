# QuestionBankImport — 任务列表

> **本模块所有任务已完成。** 以下为历史记录。

## 已完成任务

| ID | 任务 | 状态 |
|----|------|------|
| QBI-01 | 设计数据模型（document_parse_imports 表） | completed |
| QBI-02 | 创建 DocumentParseImportEntity | completed |
| QBI-03 | 创建 IDocumentParseImportRepository + 实现 | completed |
| QBI-04 | 创建 IQuestionBankImportService 接口 + record 定义 | completed |
| QBI-05 | 实现 QuestionBankImportService（4 个方法） | completed |
| QBI-06 | 创建 QuestionBankImportEndpoints（4 个端点） | completed |
| QBI-07 | 单元测试 QuestionBankImportServiceTests | completed |
| QBI-08 | DatabaseInitializer 新增 document_parse_imports 建表 SQL | completed |
| QBI-09 | DocLibraryDbContext 配置 document_parse_imports | completed |
| QBI-10 | DI 注册 IDocumentParseImportRepository（IQuestionBankImportService 未注册，见 03-DESIGN） | completed |

## 命令速查

```bash
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~QuestionBank"
```
