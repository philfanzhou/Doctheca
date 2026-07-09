# 实体关系图

## 级联行为

| 操作 | 影响范围 |
|------|---------|
| 删除 document_file | CASCADE 到 parses、parse_blocks、parse_images |
| 删除 document_parse | CASCADE 到 parse_blocks、parse_images、parse_imports |
| 删除 document_parse_image | SET NULL parse_blocks 的 image_id |
| 删除 document_parse | CASCADE 到 parse_imports（parse_id FK ON DELETE CASCADE） |
