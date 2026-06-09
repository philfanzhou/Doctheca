# 数据模型

## 核心实体

| 实体 | 用途 |
|------|------|
| `Document` | 文档元数据与生命周期 |
| `DocumentPage` | 页面级锚点 |
| `DocumentSegment` | 句子或段落级锚点 |
| `QuestionSegment` | 题目级锚点 |
| `DocumentOccurrence` | 词 / 短语出现位置 |
| `DocumentIngestionJob` | 导入任务 |

## 关键关系

- `Document` 1:N `DocumentPage`
- `Document` 1:N `DocumentSegment`
- `Document` 1:N `QuestionSegment`
- `Document` 1:N `DocumentIngestionJob`
- `DocumentSegment` / `QuestionSegment` 1:N `DocumentOccurrence`

## 不可变标识

- 文档名
- 文件哈希

## 数据用途分层

- PostgreSQL：主数据、任务、锚点、回退数据
- OpenSearch：精确检索
- Qdrant：语义召回
