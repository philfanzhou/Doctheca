# Architecture Decision Records (ADR)

本目录用于记录 DocRetrieval 服务的关键架构决策。

## 什么是 ADR？

ADR（Architecture Decision Record）是一种记录软件架构关键决策的轻量级文档，包含决策背景、备选方案、决策理由等。

## 目录结构

```text
docs/architecture/adr/
├── README.md
├── 0001-record-architecture-decisions.md
├── 0002-language-stack.md
├── 0003-vector-database.md
├── 0004-document-storage.md
└── 0005-ingestion-queue.md
```

## ADR 模板

见 [0001-record-architecture-decisions.md](./0001-record-architecture-decisions.md)。

## 如何新增 ADR

1. 复制 `0001-record-architecture-decisions.md` 作为模板
2. 按 `NNNN-title-slug.md` 格式命名（数字连续）
3. 填写所有章节内容
4. 提交 PR 评审
