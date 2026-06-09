# 排序、分页与高亮

## 排序优先级

1. 精确短语命中
2. 精确单词命中
3. 词形还原命中
4. 语义相近结果

## 去重规则

- 去重键：`document_id + page_number + sentence_id/question_id`
- 同一锚点下多次命中合并为一条结果

## 分页规则

- 使用游标分页
- 默认每页 50 条
- 最大每页 100 条
- `next_page_token` 为空表示最后一页

## 游标编码

- 使用 Base64 编码
- 内容包含排序键与锚点标识

## 高亮规则

- 返回 `start_offset` 与 `end_offset`
- 偏移以 `associated_text` 为基准
- 前端按偏移范围自行高亮，不在协议里直接返回 HTML
