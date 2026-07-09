# MinerUAgentParsing — 测试文档

## 单元测试

| 测试文件 | 覆盖范围 |
|---------|---------|
| `DocumentFileServiceTests.cs` | 文件 CRUD、元数据更新 |
| `DocumentParseBlockServiceTests.cs` | content_list.json 解析、block 插入 |
| `DocumentExportLogicTests.cs` | Markdown 路径替换、ZIP 构建、HTML 生成 |
| `PdfSplitServiceTests.cs` | PDF 页数检测、拆分 |
| `FileConversionServiceTests.cs` | LibreOffice 转换 |
| `DocumentFileDeleteCleanupTests.cs` | 删除文件时 OSS 清理 |
| `OpenSearchIndexServiceTests.cs` | BuildIndexBody / BuildSearchBody / ParseSearchResponse |
| `SearchDomainServiceTests.cs` | 搜索端点集成（含 OpenSearch 降级） |

## 集成测试（通过端点验证）

| 场景 | 端点 | 验证方式 |
|------|------|---------|
| 文件上传 | POST /admin/document-files/upload | curl + API 测试 |
| 文件列表 | GET /admin/document-files | curl |
| 触发解析 | POST /admin/document-files/{id}/parse | curl |
| 查看详情 | GET /admin/document-files/{id} | curl |
| 删除文件 | DELETE /admin/document-files/{id} | curl |
| 解析列表 | GET /admin/document-parses | curl |
| 删除解析 | DELETE /admin/document-parses/{parseId} | curl |
| 导出 MD | GET /admin/document-files/{id}/export/markdown | curl |
| 导出 HTML | GET /admin/document-files/{id}/export/html | curl |

## 关键测试场景

- 并发解析：多个文件同时触发，互不影响
- 大文档拆分：>200 页 PDF 自动拆分并合并
- 非 PDF 转换：DOCX/PPTX 通过 LibreOffice 转 PDF
- 解析失败：MinerU 返回错误时 status=failed
- OpenSearch 降级：索引失败不影响解析流程
