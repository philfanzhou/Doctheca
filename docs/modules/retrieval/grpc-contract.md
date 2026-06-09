# gRPC 协议

## 服务

- `DocumentRetrievalService.ExactSearch`
- `DocumentRetrievalService.HybridSearch`

## 请求核心字段

- `query`
- `phrase`
- `filter`
- `page_size`
- `page_token`

混合检索额外字段：

- `exact_top_k`
- `semantic_top_k`

## 返回核心字段

- `results`
- `next_page_token`
- `total_count`

单条结果包含：

- `document_name`
- `page_number`
- `associated_text`
- `score`
- `match_type`
- `segment_id`
- `start_offset`
- `end_offset`

## 错误码

- `DOCRETRIEVAL_QUERY_REQUIRED`
- `DOCRETRIEVAL_QUERY_TOO_LONG`
- `DOCRETRIEVAL_PAGE_SIZE_INVALID`
- `DOCRETRIEVAL_OPENSEARCH_UNAVAILABLE`
- `DOCRETRIEVAL_QDRANT_UNAVAILABLE`
- `DOCRETRIEVAL_EMBEDDING_FAILED`

## 协议约束

- 对外仅暴露查询能力
- 默认 `page_size = 50`
- 最大 `page_size = 100`
- `page_token` 为空表示首页
