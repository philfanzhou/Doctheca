# 系统架构

## 整体分层

1. 业务入口层
   - gRPC 查询服务
   - Web 管理界面
2. 精确检索层
   - OpenSearch
3. 语义召回层
   - Qdrant + Embedding API
4. 文档解析层
   - PDFPig / iTextSharp
   - OpenXML SDK
   - Tesseract.NET
5. 数据与存储层
   - PostgreSQL
   - SeaweedFS

## 入口关系

- 管理入口走 HTTP
- 业务入口走 gRPC
- 两个入口共享同一份文档主数据与锚点数据

## 模块关系

- 导入模块负责把原始文档转成结构化锚点与索引
- 管理模块负责维护文档生命周期与元数据一致性
- 查询模块负责把索引命中转成可解释结果

## 核心原则

- 锚点优先于聊天体验
- 精确检索是主能力
- 语义召回是增强能力
- 管理能力与查询能力分入口，不分服务
