# 精确检索设计

## 目标

把精确词命中与精确短语命中作为主检索能力。

## 技术选择

- 检索引擎：OpenSearch
- 主索引：`docretrieval-segments`
- 主要字段：
  - `document_id`
  - `document_title`
  - `subject`
  - `grade`
  - `year`
  - `page_number`
  - `block_id`
  - `sentence_id`
  - `question_id`
  - `segment_type`
  - `text`
  - `start_offset`
  - `end_offset`

## 查询策略

- 单词查询：基于 `text` 字段，启用大小写归一和词形归一
- 短语查询：基于 `text.exact` 字段，使用 `match_phrase`
- 过滤：按文档名、学科、年级、年份做精确过滤
- 高亮：基于偏移量返回命中范围

## 不采用

- 不用数据库 `LIKE` 作为主检索
- 不用向量库承担精确定位
- 不把短语查询降级成散词匹配
