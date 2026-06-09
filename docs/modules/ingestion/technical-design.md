# 文档导入技术设计

## 技术主干

- 原始文件存储：SeaweedFS
- 任务持久化：PostgreSQL `document_ingestion_jobs`
- 任务消费方式：BackgroundService + PostgreSQL 轮询
- PDF 解析：PDFPig / iTextSharp
- Word / PPT 解析：OpenXML SDK
- 扫描件 OCR：Tesseract.NET

## 处理阶段

1. 保存原始文件并写入文档记录
2. 创建导入任务记录
3. Worker 抢占 `pending` 任务并切换为 `processing`
4. 执行文本抽取 / OCR
5. 生成结构化锚点数据
6. 写入数据库
7. 同步到 OpenSearch 与 Qdrant
8. 成功后将任务标记为 `success`

## 关键设计点

- 结构化锚点是导入链路的核心产物
- OCR 只是导入链路的一部分，不单独对外暴露
- 导入队列与任务状态合一，避免双写
- 删除导入中的文档时，Worker 需要感知取消并停止后续处理

## 输出产物

- `Document`
- `DocumentPage`
- `DocumentSegment`
- `QuestionSegment`
- `DocumentOccurrence`
- `DocumentIngestionJob`

## 关联文档

- 跨模块技术主干见 [../../architecture/parsing-and-anchor-model.md](../../architecture/parsing-and-anchor-model.md)
- 存储与索引见 [../../architecture/storage-and-indexing.md](../../architecture/storage-and-indexing.md)
