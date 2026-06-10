# modules — 内部业务能力

## 功能域索引

| 域 | 功能 | 说明 | 文档 |
|----|------|------|------|
| **DocumentUpload** | DocumentUpload | 文档上传（HTTP） | [01](DocumentUpload/DocumentUpload/01-FEATURE.md) |
| **DocumentList** | DocumentList | 文档列表/详情/状态查询 | [01](DocumentList/DocumentList/01-FEATURE.md) |
| **DocumentDeletion** | DocumentDeletion | 文档删除 | [01](DocumentDeletion/DocumentDeletion/01-FEATURE.md) |
| **DocumentMetadata** | DocumentMetadata | 文档元数据更新 | [01](DocumentMetadata/DocumentMetadata/01-FEATURE.md) |
| **DocumentParsing** | DocumentParsing | PDF/DOCX 解析 | [01](DocumentParsing/DocumentParsing/01-FEATURE.md) |
| **ExactSearch** | ExactSearch | 精确关键词搜索 | [01](ExactSearch/ExactSearch/01-FEATURE.md) |
| **HybridSearch** | HybridSearch | 混合搜索（关键词+语义） | [01](HybridSearch/HybridSearch/01-FEATURE.md) |

## 按层次查看

### gRPC 搜索（对外服务）

- [ExactSearch](ExactSearch/ExactSearch/01-FEATURE.md) — `DocumentRetrievalServiceImpl.ExactSearch`
- [HybridSearch](HybridSearch/HybridSearch/01-FEATURE.md) — `DocumentRetrievalServiceImpl.HybridSearch`

### HTTP Admin API（内部管理）

- [DocumentUpload](DocumentUpload/DocumentUpload/01-FEATURE.md) — `DocumentAdminEndpoints.UploadDocument`
- [DocumentList](DocumentList/DocumentList/01-FEATURE.md) — `DocumentAdminEndpoints.ListDocuments / GetDocument / GetDocumentStatus`
- [DocumentDeletion](DocumentDeletion/DocumentDeletion/01-FEATURE.md) — `DocumentAdminEndpoints.DeleteDocument`
- [DocumentMetadata](DocumentMetadata/DocumentMetadata/01-FEATURE.md) — `DocumentAdminEndpoints.UpdateDocumentMetadata`

### 后台处理

- [DocumentParsing](DocumentParsing/DocumentParsing/01-FEATURE.md) — `IngestionWorker` + `DocumentParserService`