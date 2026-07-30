# Document Library

Document Library 定义教育文档及其可搜索解析结果。它不拥有 Question Catalog 的导入状态或题目。

## Language

**Document File**:
进入文档库并作为解析来源的完整文档。
_Avoid_: Question、Upload Record

**Document Parse**:
一次把 Document File 转换为结构化内容的结果。
_Avoid_: Document File、Import Job

**Parse Block**:
Document Parse 中可独立检索和定位的内容单元。
_Avoid_: Question Content、Page

**Parse Image**:
Document Parse 从原文档提取并保留关联的图片。
_Avoid_: Learner Image、Question Image
