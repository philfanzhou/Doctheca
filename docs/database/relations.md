# 实体关系图

```
┌─────────────────────────────────────────────────────────────┐
│                        documents                            │
│  PK: id                                                     │
│  title, file_hash, file_path, source_type, file_size        │
│  language, grade, subject, year, tags, status               │
└───────┬───────────┬───────────┬──────────────────┬──────────┘
        │ 1:N       │ 1:N       │ 1:N              │ 1:N
        │ CASCADE   │ CASCADE   │ CASCADE          │ CASCADE
        ▼           ▼           ▼                  ▼
┌───────────┐ ┌───────────┐ ┌───────────────┐ ┌──────────────────────┐
│ doc_pages │ │ doc_segs  │ │ question_segs │ │ doc_ingestion_jobs   │
│ PK: id    │ │ PK: id    │ │ PK: id        │ │ PK: id               │
│ FK: doc_id│ │ FK: doc_id│ │ FK: doc_id    │ │ FK: doc_id           │
│ page_num  │ │ FK: pg_id │ │ FK: page_id   │ │ status, parser_ver   │
│ img_path  │ │ text      │ │ stem, options │ │ error_msg, times     │
└─────┬─────┘ └─────┬─────┘ └────┬──────────┘ └──────────────────────┘
      │             │            │
      │ 1:N CASCADE │ SET NULL   │ SET NULL
      │             ▼            ▼
      │       ┌─────────────────────────────────────┐
      │       │        document_occurrences         │
      │       │  PK: id, FK: doc_id (CASCADE)       │
      │       │  FK: segment_id (SET NULL)          │
      └──────►│  FK: question_segment_id (SET NULL) │
              │  token_text, token_stem, offsets    │
              └─────────────────────────────────────┘
```

## 级联行为

| 操作 | 影响范围 |
|------|---------|
| 删除 document | CASCADE 到 pages, segments, question_segments, occurrences, ingestion_jobs |
| 删除 document_page | CASCADE 到 segments, question_segments；SET NULL occurrences 的外键 |
| 删除 segment | SET NULL 对应 occurrences 的 segment_id |
| 删除 question_segment | SET NULL 对应 occurrences 的 question_segment_id |
| 删除 document_file | CASCADE 到 parses、parse_blocks、parse_images |
| 删除 document_parse | CASCADE 到 parse_blocks、parse_images |
| 删除 document_parse_image | SET NULL parse_blocks 的 image_id |