# Requirements — 服务级需求

> 本文件是需求摘要入口。详细功能需求见 [modules/](../modules/README.md) 目录。

## 功能需求

### 文档管理与解析

| 编号 | 需求 | 详见 |
|------|------|------|
| FR-01 | 文档管理：上传文档文件（PDF/DOC/DOCX/PPT/PPTX），文件存入 OSS，记录存入 `document_files` 表；列表/详情/元数据更新/删除（联动解析、图片、索引） | [DocumentManagement](../modules/DocumentManagement/01-FEATURE.md) |
| FR-02 | MinerU 文档解析：后台 Worker 调用 MinerU Precision API 解析文件，结果持久化到 `document_parses` / `document_parse_blocks` / `document_parse_images`（含 doc-converter/PdfSplit 转档） | [DocumentParse](../modules/DocumentParse/01-FEATURE.md) |
| FR-03 | 文档导出：文件级/解析级的 Markdown/HTML 导出，图片路径支持相对/Base64/Presigned 三种模式，支持 ZIP 打包 | [DocumentExport](../modules/DocumentExport/01-FEATURE.md) |
| FR-04 | 文档元数据分析：MinerU 解析完成后，文档缺 `subject`/`grade`/`year` 时由 LLM best-effort 自动填充 | [DocumentMetadataAnalysis](../modules/DocumentMetadataAnalysis/01-FEATURE.md) |
| FR-05 | 更新文档元数据：通过 `PUT /admin/document-files/{id}/metadata` 修改 `subject`/`grade`/`year`，同步刷新 OpenSearch 索引 | [DocumentSearch](../modules/DocumentSearch/01-FEATURE.md) |
| FR-06 | 精确搜索：基于 OpenSearch 的关键词匹配，支持 subject/grade/year/documentTitle 过滤；含索引写入（解析完成后自动索引） | [DocumentSearch](../modules/DocumentSearch/01-FEATURE.md) |
| FR-07 | 管理员认证：使用 Identity bootstrap 管理员登录，所有浏览器管理 API 要求有效 `role=admin`，Token 仅存于 HttpOnly Cookie | [AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md) |

## 非功能需求

| 编号 | 需求 |
|------|------|
| NFR-01 | 搜索响应时间 < 2s（精确搜索） |
| NFR-02 | 文档解析异步执行（MinerUFileParseWorker），不阻塞上传响应 |
| NFR-03 | OpenSearch 索引失败不阻塞解析主流程，仅记日志 |
| NFR-04 | 数据库使用 PostgreSQL（代码硬编码 `UseNpgsql`） |
| NFR-05 | 服务单端口：HTTP(5012) |
| NFR-06 | 管理认证校验 JWT issuer、audience、签名和有效期；普通用户 JWT 必须返回 403 |
| NFR-07 | SPA、静态文件和健康检查保持匿名，管理 API 必须使用 Identity 管理员身份 |
