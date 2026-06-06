# DocRetrieval 服务规格说明

> 本文档为 `ruoyu.docretrieval` 服务的正式规格说明（Spec），**只描述"怎么做"**。
> 业务视角的需求（做什么、为谁做、做到什么程度）请参考 [requirements.md](./requirements.md)；
> 业务流程（谁在什么时机做什么、状态如何流转）请参考 [process.md](./process.md)。
>
> 内容重构自讨论计划：[../plan/english-doc-rag-scheme-plan.md](../../../plan/english-doc-rag-scheme-plan.md)。
> 配套文档：[requirements.md](./requirements.md)（业务需求）、[process.md](./process.md)（业务流程）、[checklist.md](./checklist.md)（验收清单）、[tasks.md](./tasks.md)（实施任务）。

## 1. 概述

### 1.1 服务名称与定位

- 服务目录：`backend/ruoyu.docretrieval/`
- 服务命名空间：`Ruoyu.Study.DocRetrieval`
- 解决方案：`Ruoyu.Study.DocRetrieval.sln`
- 定位：独立的**文档检索域**微服务，自带 Web 管理界面用于文档管理，对外通过 gRPC 暴露查询定位能力。

### 1.2 服务端口分配

| 端口 | 协议 | 用途 |
|------|------|------|
| 5011 | gRPC | 内部服务间查询调用 |
| 5012 | HTTP | Web 管理界面 + 管理 API |

> 端口分配遵循现有微服务编号规则（5001~5010 已占用），已在 [系统架构文档](../../architecture.md) 中登记。

### 1.3 业务边界速览

> 业务细节、调用方故事、验收口径见 [requirements.md](./requirements.md)；业务流程见 [process.md](./process.md)。本节只给技术读者一个"这个服务要解决什么"的最短描述。

- 接收英文教材与考试真题的 `PDF`（含扫描件）/ `Word` / `PPT`
- 解析出"文档展示锚点"作为一等数据
- 提供**精确检索**与**语义扩展**两类检索能力
- 自带 Web 管理界面用于文档上传 / 删除 / 查看状态
- 对外（业务系统）仅通过 gRPC 暴露查询定位接口
- **不在本服务范围**：聊天式问答、错题 / 作业业务、单词释义、账号与权限

### 1.3 术语表

| 术语 | 含义 |
|------|------|
| 文档（Document） | 一份 PDF（含扫描件）/ Word / PPT，对应一个导入任务 |
| 锚点（Anchor） | 文档内可定位的最小单位（页/块/句/题） |
| 精确检索 | 基于倒排索引的单词/短语匹配 |
| 语义召回 | 基于向量相似度的近义表达召回 |
| 混合检索 | 同时返回精确匹配与语义匹配结果 |

---

## 2. 现状与约束

### 2.1 仓库现状

- 仓库中无已落地的文档检索、向量检索、Embedding、PDF/Word 解析实现
- 现有 AI 能力集中在 `ruoyu.student` 的视觉模型调用（图片分类），不是文档知识库
- 所有后端服务均为 `.NET + gRPC`，暂未引入搜索引擎或向量库

### 2.2 技术约束

| 约束 | 取值 |
|------|------|
| 文档来源 | 电子文本（PDF / Word / PPT）+ 扫描件，均占比大 |
| 检索目标 | 精确位置 + 语义扩展 |
| 规模 | 中规模（一年内数千份） |
| 主语言 | 全 .NET（与现有微服务一致，详见 [ADR 0002](./adr/0002-language-stack.md)） |
| 对外协议 | gRPC（与现有微服务一致，仅查询接口） |
| Web 管理界面 | 本服务自带，用于上传 / 删除 / 查看导入状态 |

---

## 3. 目标架构

### 3.1 整体分层

```
┌──────────────────────────────────────────────────────┐
│  4. 业务入口层                                        │
│  ┌────────────────────┐  ┌──────────────────────┐    │
│  │ gRPC 查询服务       │  │ Web 管理界面 (HTTP)   │    │
│  │ (业务系统调用)       │  │ (管理员/运营人员使用) │    │
│  └────────────────────┘  └──────────────────────┘    │
└──────────────────────┬───────────────────────────────┘
            ↑                       ↑
┌───────────┴──────────┐  ┌────────┴────────────────┐
│  2. 精确检索层        │  │  3. 语义召回层           │
│  (OpenSearch)        │  │  (Qdrant)                │
└──────────────────────┘  └─────────────────────────┘
            ↑                       ↑
┌───────────┴───────────────────────┴────────────────┐
│  1. 文档解析层  (.NET 工具链)                        │
│  - PDFPig / iTextSharp (PDF) + OpenXML SDK (Word/PPT)│
│  - Tesseract.NET (扫描件 OCR)                       │
│  - 产出结构化 JSON（含锚点）                          │
└──────────────────────────────────────────────────────┘
```

### 3.2 第 1 层：文档解析层

**输入**：

- `PDF`（含扫描型）
- `Word`（`.doc` / `.docx`）
- `PPT`（`.ppt` / `.pptx`）

**职责**：

- 抽取原始文本
- 保留页码、段落、块、行级位置信息
- 识别句子边界
- 对试卷识别题目边界（题号、题干、选项、答案区域）

**工具方向**（全 .NET，详见 [ADR 0002](./adr/0002-language-stack.md)）：

- PDF 原生抽取：`PDFPig`（首选）或 `iTextSharp`
- Word / PPT 抽取：`OpenXML SDK`
- 扫描件 OCR：`Tesseract.NET`（英文识别率可接受）

**结构化产物（必须）**：

```json
{
  "document_id": "doc-001",
  "page": 12,
  "block_id": "p12-b03",
  "sentence_id": "p12-b03-s02",
  "question_id": "q15",
  "text": "The students were encouraged to take notes.",
  "tokens": [
    { "text": "The", "start": 0, "end": 3 },
    { "text": "students", "start": 4, "end": 12 }
  ]
}
```

**关键原则**：

- "文档展示锚点"是一等数据，不只存 chunk 文本
- 结构化中间层是正式主干

#### 3.2.1 文档解析规则

**英文句子边界识别**：

- 基于 PDFPig / OpenXML 输出的行级位置信息，合并同一块内的连续行为段落
- 句子边界判定规则：
  1. 以 `. ` / `! ` / `? ` 结尾的行视为句子结束
  2. 缩写不视为句子结束：`Mr.` / `Mrs.` / `Ms.` / `Dr.` / `Prof.` / `Sr.` / `Jr.` / `vs.` / `etc.` / `e.g.` / `i.e.` / `U.S.` / `U.K.` — 维护缩写白名单，识别到白名单中的缩写时跳过断句
  3. 引号内的句号不视为句子结束：`"...text."` → 不在引号内句号后断句
  4. 数字中的句号不视为句子结束：`3.14` / `2026.06.01`
- 句子 ID 生成规则：`p{页码}-b{块序号}-s{句子序号}`（如 `p12-b03-s02`）

**试卷题目边界识别**：

- 题号正则：`/^\s*(\d+)\s*[.、．)\]】]/` — 匹配行首的数字编号（如 `1.` / `2、` / `3)` / `4]`）
- 选项正则：`/^\s*([A-Da-d])\s*[.、．)\]】]/` — 匹配行首的选项编号（如 `A.` / `B、` / `C)`）
- 题目结构：
  - `stem`：题号到第一个选项之间的文本
  - `options`：所有选项文本，以 JSON 数组存储
  - `answer_area`：最后一个选项到下一题号之间的文本（可能包含答案）
- 题目 ID 生成规则：`q{题号}`（如 `q15`）

**扫描件 OCR 后处理**：

- Tesseract.NET 输出文本后，执行以下后处理：
  1. 合并被换行符打断的单词（行尾 `-` 连字符）
  2. 移除多余的空白字符（多个空格合并为一个）
  3. 修正常见 OCR 错误（`0` ↔ `O`、`1` ↔ `l`、`5` ↔ `S`）— 仅在上下文明确为英文单词时修正
- OCR 置信度低于阈值（默认 60%）的页面，在 `DocumentIngestionJob.error_message` 中记录警告，但不阻止导入

**Token 分词规则**：

- 使用 OpenSearch 的 `standard` tokenizer 进行分词（与索引分析器一致）
- Token 记录原始文本（`token_text`）和词干还原后文本（`token_stem`）
- 词干还原使用 Porter Stemmer 算法（与 OpenSearch `english` stemmer 一致）
- 标点符号不作为独立 token 记录

### 3.3 第 2 层：精确检索层（主能力）

**职责**：

- 精确搜索单词
- 精确搜索短语
- 大小写归一、词形归一
- 返回页码、段落、句子、题号
- 高亮命中范围

**推荐**：`OpenSearch`（或 `Elasticsearch`）

**原因**：

- 对中规模文档库更稳
- 原生支持全文检索、高亮、短语查询、BM25、过滤条件组合
- 比只用向量库更适合"查出现位置"
- 后续"按教材/年级/题型/年份"过滤更自然

**不推荐**：

- 单纯向量库（不适合精确词位查询）
- 数据库 `LIKE`（准确性、性能、可扩展性都不够）
- `pgvector`（解决语义相似，不解决全文定位）

#### 3.3.1 OpenSearch 索引设计

**索引名称**：`docretrieval-segments`

**索引 Mapping**：

```json
{
  "settings": {
    "index": {
      "number_of_shards": 1,
      "number_of_replicas": 0
    },
    "analysis": {
      "analyzer": {
        "english_custom": {
          "type": "custom",
          "tokenizer": "standard",
          "filter": ["lowercase", "english_stop", "english_stemmer"]
        },
        "english_phrase": {
          "type": "custom",
          "tokenizer": "standard",
          "filter": ["lowercase"]
        }
      },
      "filter": {
        "english_stop": {
          "type": "stop",
          "stopwords": "_english_"
        },
        "english_stemmer": {
          "type": "stemmer",
          "language": "english"
        }
      }
    }
  },
  "mappings": {
    "properties": {
      "document_id":    { "type": "keyword" },
      "document_title": { "type": "keyword" },
      "subject":        { "type": "keyword" },
      "grade":          { "type": "keyword" },
      "year":           { "type": "keyword" },
      "page_number":    { "type": "integer" },
      "block_id":       { "type": "keyword" },
      "sentence_id":    { "type": "keyword" },
      "question_id":    { "type": "keyword" },
      "segment_type":   { "type": "keyword" },
      "text": {
        "type": "text",
        "analyzer": "english_custom",
        "fields": {
          "exact": {
            "type": "text",
            "analyzer": "english_phrase"
          },
          "keyword": {
            "type": "keyword",
            "ignore_above": 256
          }
        }
      },
      "start_offset":   { "type": "integer" },
      "end_offset":     { "type": "integer" },
      "created_at":     { "type": "date" }
    }
  }
}
```

**字段说明**：

| 字段 | 用途 |
|------|------|
| `document_id` | 文档唯一标识，用于过滤和删除时按文档清理 |
| `document_title` | 文档名，用于按文档名过滤查询结果 |
| `subject` / `grade` / `year` | 元数据过滤维度，使用 keyword 类型支持精确过滤 |
| `page_number` | 页码，返回给调用方 |
| `block_id` / `sentence_id` / `question_id` | 锚点标识，用于还原到文档精确位置 |
| `segment_type` | 段落类型：`sentence`（句子）/ `question`（题目），决定关联文本的粒度 |
| `text` | 全文检索主字段，使用 `english_custom` 分析器实现词形归一 |
| `text.exact` | 短语查询专用，仅做小写归一不做词干提取，保证短语匹配精确性 |
| `text.keyword` | 精确匹配备用 |
| `start_offset` / `end_offset` | 文本偏移，用于高亮 |

**查询策略**：

- **单词查询**：使用 `text` 字段 + `english_custom` 分析器，自动实现大小写归一和词形归一（如 study/studies/studied 互查）
- **短语查询**：使用 `text.exact` 字段 + `match_phrase`，保证短语不被拆碎
- **过滤**：使用 `term` / `terms` 查询对 `subject` / `grade` / `year` / `document_title` 进行精确过滤
- **高亮**：使用 `unified` highlighter，基于 `start_offset` / `end_offset` 返回精确偏移

**分片策略**：

- 初始 1 主分片 + 0 副本（中规模数据，单节点部署）
- 数据量超过 50GB 或写入 QPS 超过单分片承载时，扩至 3~5 分片

### 3.4 第 3 层：语义召回层

**职责**：

- 查相近表达
- 查意思相近的句子/题目
- 查某词在不同题型中的变体考法

**选型**：`Qdrant`（详见 [ADR 0003](./adr/0003-vector-database.md)）

**切分粒度**：

- 句子级
- 题目级
- 必要时段落级

**禁止**：只做粗粒度 chunk（无法满足"返回哪一句/哪一道题"）。

#### 3.4.1 Embedding 模型选型

**选型**：`BAAI/bge-large-en-v1.5`（通过 SiliconFlow Embedding API）

| 参数 | 值 |
|------|-----|
| 模型 | `BAAI/bge-large-en-v1.5` |
| 部署方式 | SiliconFlow Embedding API（`https://api.siliconflow.cn/v1/embeddings`） |
| 向量维度 | 1024 |
| 距离度量 | Cosine |
| 最大输入长度 | 512 tokens |
| 调用方式 | .NET 通过 `https://api.siliconflow.cn/v1/embeddings` REST API 调用 |

**选型理由**：

- `bge-large-en-v1.5` 英文语义召回质量在开源模型中处于第一梯队
- 通过 SiliconFlow Embedding API 调用，无需本地部署推理服务，降低运维复杂度
- 1024 维向量在召回质量和存储成本之间取得平衡
- 与现有 SiliconFlow API 统一：VL 分析和 Embedding 均通过 SiliconFlow 调用

**备选方案**：

- 若需更小向量维度以降低存储，可换用 `all-MiniLM-L6-v2`（384 维），但英文语义质量会下降

**批量 Embedding 策略**：

- 导入时按文档粒度批量调用：解析完一个文档的所有 segment 后，通过 SiliconFlow REST API 批量发送 Embedding 请求
- 批量大小：16 条/批
- 失败重试：3 次，指数退避

#### 3.4.2 Qdrant Collection 设计

**Collection 名称**：`docretrieval_segments`

**Collection 配置**：

```json
{
  "vectors": {
    "size": 1024,
    "distance": "Cosine",
    "on_disk": true
  },
  "optimizers_config": {
    "indexing_threshold": 20000
  }
}
```

**Payload Schema**：

| 字段 | 类型 | 用途 |
|------|------|------|
| `document_id` | keyword | 文档唯一标识，用于过滤和删除 |
| `document_title` | keyword | 文档名，用于过滤 |
| `subject` | keyword | 学科，用于过滤 |
| `grade` | keyword | 年级，用于过滤 |
| `year` | keyword | 年份，用于过滤 |
| `page_number` | integer | 页码 |
| `block_id` | keyword | 块标识 |
| `sentence_id` | keyword | 句子标识 |
| `question_id` | keyword | 题目标识 |
| `segment_type` | keyword | 段落类型：`sentence` / `question` |
| `text` | text | 原始文本，用于返回关联文本 |
| `start_offset` | integer | 文本起始偏移 |
| `end_offset` | integer | 文本结束偏移 |

**Payload 索引**（用于过滤加速）：

```json
{
  "field_indexes": {
    "document_id": { "type": "keyword" },
    "subject": { "type": "keyword" },
    "grade": { "type": "keyword" },
    "year": { "type": "keyword" },
    "segment_type": { "type": "keyword" }
  }
}
```

**查询策略**：

- 语义查询时，先对 query 文本做 Embedding，再在 Qdrant 中做 `search` + `filter` 组合
- `semantic_top_k` 默认 20，由 `HybridSearchRequest` 参数控制
- 返回结果与 OpenSearch 精确检索结果合并去重（按 `document_id + page_number + sentence_id/question_id` 去重键）

### 3.5 第 4 层：业务入口层

本层包含**两个独立入口**：

#### 3.5.1 gRPC 查询服务（供业务系统调用）

**职责**：

- 接收业务系统的查询请求（单词 / 短语 + 可选过滤）
- 聚合精确检索 + 语义召回结果
- 按规则排序
- 返回带锚点的命中记录

**返回协议**：见 §5.3 proto 定义（`SearchResponse`）。

**结果排序优先级**：

1. 精确短语命中
2. 精确单词命中
3. 词形还原命中
4. 语义相近结果

#### 3.5.2 Web 管理界面（供管理员 / 运营人员使用）

**功能**：

- 上传文档（PDF / Word / PPT）+ 填写元数据
- 查看导入任务状态（进行中 / 成功 / 失败）
- 删除文档（硬删除）
- 查看已导入文档列表

**技术方向**：

- 本服务内置 HTTP 端点（如 ASP.NET Core MVC / Razor Pages / Blazor Server）
- 前端可考虑轻量方案（如 HTMX + Alpine.js 或 Blazor Server）
- 鉴权由基础设施层（网关 / 反向代理）负责

---

## 4. 数据模型

### 4.1 核心实体

| 实体 | 作用 | 关键字段 |
|------|------|----------|
| `Document` | 文档元数据 | id, title, source_type, file_hash, language, grade, subject, year, tags, status, created_at, updated_at |
| `DocumentPage` | 页面级锚点 | id, document_id, page_number, image_path |
| `DocumentSegment` | 句子/段落级锚点 | id, document_id, page_id, block_id, sentence_id, text, start_offset, end_offset |
| `QuestionSegment` | 题目锚点 | id, document_id, page_id, question_id, stem, options_json, answer_area |
| `DocumentOccurrence` | 词/短语出现记录 | id, document_id, segment_id, token_text, start_offset, end_offset |
| `DocumentIngestionJob` | 导入任务 | id, document_id, status, parser_version, ocr_version, error_message, started_at, finished_at |

### 4.2 数据库 Schema 详细设计

> 数据库命名遵循项目现有惯例：EF Core 实体使用 PascalCase，数据库列使用 snake_case（通过 EF Core 约定或 `[Column]` 特性映射）。
> 数据库名称：`ruoyu_study_docretrieval`（遵循 [database-spec.md](../../database-spec.md) 命名规范）。

#### 4.2.1 `documents` 表

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `title` | VARCHAR(200) | UNIQUE, NOT NULL | 文档名（业务唯一标识） |
| `source_type` | VARCHAR(20) | NOT NULL | 文档类型：`pdf` / `word` / `ppt` |
| `file_hash` | VARCHAR(64) | UNIQUE, NOT NULL | 文件内容 SHA-256 哈希（防重复导入） |
| `file_path` | VARCHAR(500) | NOT NULL | SeaweedFS 中的原始文件路径 |
| `file_size` | BIGINT | NOT NULL | 文件大小（字节） |
| `language` | VARCHAR(10) | NOT NULL, DEFAULT 'en' | 文档语言，当前仅 `en` |
| `grade` | VARCHAR(20) | NOT NULL | 年级（取值见 GradeConstants，如 "高一"） |
| `subject` | VARCHAR(20) | NOT NULL | 学科（当前仅 "英语"） |
| `year` | VARCHAR(10) | NOT NULL | 年份（如 "2023"） |
| `tags` | TEXT | NULL | 标签，JSON 数组格式（如 `["高考","全国卷"]`） |
| `status` | VARCHAR(20) | NOT NULL, DEFAULT 'pending' | 文档状态：`pending` / `processing` / `ready` / `failed` |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |
| `updated_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 更新时间 |

**索引**：

- `uk_documents_title`：`title` 唯一索引（防重名）
- `uk_documents_file_hash`：`file_hash` 唯一索引（防重复文件）
- `idx_documents_subject_grade_year`：`(subject, grade, year)` 复合索引（查询过滤）
- `idx_documents_status`：`status` 索引（按状态筛选）

#### 4.2.2 `document_pages` 表

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `document_id` | UUID | FK → documents.id, NOT NULL | 所属文档 |
| `page_number` | INTEGER | NOT NULL | 页码（从 1 开始） |
| `image_path` | VARCHAR(500) | NULL | 页面图片路径（扫描件 OCR 前的页面快照） |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |

**索引**：

- `uk_document_pages_doc_page`：`(document_id, page_number)` 唯一索引
- `fk_document_pages_document`：`document_id` 外键，ON DELETE CASCADE

#### 4.2.3 `document_segments` 表

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `document_id` | UUID | FK → documents.id, NOT NULL | 所属文档 |
| `page_id` | UUID | FK → document_pages.id, NOT NULL | 所属页面 |
| `block_id` | VARCHAR(50) | NOT NULL | 块标识（如 `p12-b03`） |
| `sentence_id` | VARCHAR(50) | NOT NULL | 句子标识（如 `p12-b03-s02`） |
| `segment_type` | VARCHAR(20) | NOT NULL, DEFAULT 'sentence' | 段落类型：`sentence` / `paragraph` |
| `text` | TEXT | NOT NULL | 句子/段落原文 |
| `start_offset` | INTEGER | NOT NULL | 在页面文本中的起始偏移 |
| `end_offset` | INTEGER | NOT NULL | 在页面文本中的结束偏移 |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |

**索引**：

- `uk_document_segments_doc_sentence`：`(document_id, sentence_id)` 唯一索引
- `fk_document_segments_document`：`document_id` 外键，ON DELETE CASCADE
- `fk_document_segments_page`：`page_id` 外键，ON DELETE CASCADE

#### 4.2.4 `question_segments` 表

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `document_id` | UUID | FK → documents.id, NOT NULL | 所属文档 |
| `page_id` | UUID | FK → document_pages.id, NOT NULL | 所属页面 |
| `question_id` | VARCHAR(50) | NOT NULL | 题号标识（如 `q15`） |
| `stem` | TEXT | NOT NULL | 题干文本 |
| `options_json` | TEXT | NULL | 选项 JSON（如 `[{"label":"A","text":"..."}]`） |
| `answer_area` | TEXT | NULL | 答案区域文本 |
| `start_offset` | INTEGER | NOT NULL | 在页面文本中的起始偏移 |
| `end_offset` | INTEGER | NOT NULL | 在页面文本中的结束偏移 |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |

**索引**：

- `uk_question_segments_doc_question`：`(document_id, question_id)` 唯一索引
- `fk_question_segments_document`：`document_id` 外键，ON DELETE CASCADE
- `fk_question_segments_page`：`page_id` 外键，ON DELETE CASCADE

#### 4.2.5 `document_occurrences` 表

> 词/短语出现记录，用于精确检索时快速定位命中位置。每个 token 在文档中的每次出现对应一条记录。

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `document_id` | UUID | FK → documents.id, NOT NULL | 所属文档 |
| `segment_id` | UUID | FK → document_segments.id, NULL | 所属句子段落（NULL 表示该出现属于题目） |
| `question_segment_id` | UUID | FK → question_segments.id, NULL | 所属题目段落（NULL 表示该出现属于句子） |
| `token_text` | VARCHAR(200) | NOT NULL | 原始 token 文本 |
| `token_stem` | VARCHAR(200) | NOT NULL | 词干还原后的文本（用于词形归一检索） |
| `start_offset` | INTEGER | NOT NULL | 在所属段落文本中的起始偏移 |
| `end_offset` | INTEGER | NOT NULL | 在所属段落文本中的结束偏移 |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |

**索引**：

- `idx_document_occurrences_doc_token`：`(document_id, token_text)` 复合索引
- `idx_document_occurrences_stem`：`(document_id, token_stem)` 复合索引
- `fk_document_occurrences_document`：`document_id` 外键，ON DELETE CASCADE
- `fk_document_occurrences_segment`：`segment_id` 外键，ON DELETE SET NULL
- `fk_document_occurrences_question`：`question_segment_id` 外键，ON DELETE SET NULL

> **注意**：`document_occurrences` 表是精确检索的辅助加速表。主要检索仍依赖 OpenSearch 倒排索引，此表用于在 OpenSearch 不可用或需要精确偏移校验时提供回退。

#### 4.2.6 `document_ingestion_jobs` 表

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `document_id` | UUID | FK → documents.id, NOT NULL | 所属文档 |
| `status` | VARCHAR(20) | NOT NULL, DEFAULT 'pending' | 任务状态：`pending` / `processing` / `success` / `failed` |
| `parser_version` | VARCHAR(20) | NULL | 解析器版本号 |
| `ocr_version` | VARCHAR(20) | NULL | OCR 引擎版本号 |
| `error_message` | TEXT | NULL | 失败原因 |
| `started_at` | TIMESTAMPTZ | NULL | 开始处理时间 |
| `finished_at` | TIMESTAMPTZ | NULL | 处理完成时间 |
| `created_at` | TIMESTAMPTZ | NOT NULL, DEFAULT NOW() | 创建时间 |

**索引**：

- `idx_ingestion_jobs_status`：`status` 索引（按状态筛选任务）
- `fk_ingestion_jobs_document`：`document_id` 外键，ON DELETE CASCADE

### 4.3 结构化锚点模型

锚点模型是系统的核心抽象。所有检索结果必须可还原到：

- 文档（哪本书/哪份试卷）
- 页码
- 块 / 段 / 句 / 题号
- 文本偏移（用于高亮）

---

## 5. 接口设计

### 5.1 gRPC 服务清单（仅查询能力）

| RPC | 方法 | 用途 | 调用方 |
|-----|------|------|--------|
| `ExactSearch` | `POST` 风格 | 精确词/短语检索 | 业务系统 |
| `HybridSearch` | `POST` 风格 | 混合检索（精确 + 语义） | 业务系统 |

### 5.2 Web 管理接口（HTTP，本服务自带）

| 端点 | 方法 | 用途 | 使用者 |
|------|------|------|--------|
| `/admin/documents/upload` | `POST` (multipart) | 上传文档（必填：title / subject / grade / year） | Web 管理界面 |
| `/admin/documents` | `GET` | 查看已导入文档列表（支持分页 / 按 title 模糊搜索 / 按 subject / grade / year / status 筛选） | Web 管理界面 |
| `/admin/documents/{id}/status` | `GET` | 查看导入任务状态 | Web 管理界面 |
| `/admin/documents/{title}` | `DELETE` | 删除文档（硬删除，按文档名） | Web 管理界面 |
| `/admin/documents/{title}/metadata` | `PUT` | 修改文档元数据（学科/年级/年份/标签） | Web 管理界面 |

**文档列表查询参数**：

| 参数 | 类型 | 说明 |
|------|------|------|
| `keyword` | query string | 按 `title` 模糊搜索 |
| `subject` | query string | 按学科精确筛选 |
| `grade` | query string | 按年级精确筛选 |
| `year` | query string | 按年份精确筛选 |
| `status` | query string | 按导入状态筛选（pending / processing / success / failed / deleted） |
| `page` | query string | 页码（从 1 开始） |
| `pageSize` | query string | 每页条数（默认 20） |

#### 5.2.1 Web 管理 API 请求/响应体

**上传文档 `POST /admin/documents/upload`**

- Content-Type：`multipart/form-data`
- 文件大小限制：单文件最大 200MB
- 支持的 MIME 类型：`application/pdf`、`application/msword`、`application/vnd.openxmlformats-officedocument.wordprocessingml.document`、`application/vnd.ms-powerpoint`、`application/vnd.openxmlformats-officedocument.presentationml.presentation`

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `file` | file | 是 | 文档文件 |
| `title` | string | 是 | 文档名（1~200 字符） |
| `subject` | string | 是 | 学科（当前仅 "英语"） |
| `grade` | string | 是 | 年级（取值见 GradeConstants） |
| `year` | string | 是 | 年份（如 "2023"） |
| `tags` | string | 否 | 标签，JSON 数组字符串（如 `["高考","全国卷"]`） |

成功响应（200）：

```json
{
  "success": true,
  "data": {
    "document_id": "uuid",
    "title": "人教版高中英语必修一",
    "job_id": "uuid",
    "status": "pending"
  }
}
```

失败响应示例：

```json
{
  "success": false,
  "message": "文档名已存在",
  "errorCode": "DOCRETRIEVAL_TITLE_ALREADY_EXISTS"
}
```

**文档列表 `GET /admin/documents`**

成功响应（200）：

```json
{
  "success": true,
  "data": [
    {
      "id": "uuid",
      "title": "人教版高中英语必修一",
      "source_type": "pdf",
      "subject": "英语",
      "grade": "高一",
      "year": "2023",
      "tags": ["高考"],
      "status": "ready",
      "created_at": "2026-06-01T10:00:00Z",
      "updated_at": "2026-06-01T10:05:00Z"
    }
  ],
  "total": 100,
  "page": 1,
  "pageSize": 20,
  "totalPages": 5
}
```

**查看导入状态 `GET /admin/documents/{id}/status`**

成功响应（200）：

```json
{
  "success": true,
  "data": {
    "document_id": "uuid",
    "title": "人教版高中英语必修一",
    "status": "ready",
    "jobs": [
      {
        "job_id": "uuid",
        "status": "success",
        "parser_version": "1.0.0",
        "ocr_version": "5.0",
        "error_message": null,
        "started_at": "2026-06-01T10:00:00Z",
        "finished_at": "2026-06-01T10:05:00Z"
      }
    ]
  }
}
```

**删除文档 `DELETE /admin/documents/{title}`**

成功响应（200）：

```json
{
  "success": true,
  "data": {
    "title": "人教版高中英语必修一",
    "deleted": true
  }
}
```

> 删除是幂等的：文档已不存在时仍返回 `success: true`，`deleted: false`。

**修改元数据 `PUT /admin/documents/{title}/metadata`**

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `subject` | string | 否 | 学科 |
| `grade` | string | 否 | 年级 |
| `year` | string | 否 | 年份 |
| `tags` | string | 否 | 标签，JSON 数组字符串 |

> 至少提供一项，文档名和文件哈希不可修改。

成功响应（200）：

```json
{
  "success": true,
  "data": {
    "id": "uuid",
    "title": "人教版高中英语必修一",
    "subject": "英语",
    "grade": "高二",
    "year": "2024",
    "tags": ["高考"]
  }
}
```

### 5.3 gRPC 消息定义

```protobuf
syntax = "proto3";

package ruoyu.study.docretrieval.v1;

option csharp_namespace = "Ruoyu.Study.DocRetrieval.Contract.Protos";

// 查询过滤条件
message SearchFilter {
  string document_title = 1;   // 按文档名精确过滤
  string subject = 2;          // 按学科过滤（如 "英语"）
  string grade = 3;            // 按年级过滤（如 "高一"，取值见 GradeConstants）
  string year = 4;             // 按年份过滤（如 "2023"）
}

// 精确检索请求
message ExactSearchRequest {
  string query = 1;            // 要查的单词或短语（必填，1~200 字符）
  bool phrase = 2;             // true=短语查询（不拆碎），false=单词查询
  SearchFilter filter = 3;     // 可选过滤条件
  int32 page_size = 4;         // 每页条数，默认 50，最大 100
  string page_token = 5;       // 游标，首页传空
}

// 混合检索请求（精确 + 语义）
message HybridSearchRequest {
  string query = 1;            // 要查的单词或短语（必填，1~200 字符）
  bool phrase = 2;             // true=短语查询，false=单词查询
  int32 exact_top_k = 3;       // 精确检索返回条数上限，默认 50
  int32 semantic_top_k = 4;    // 语义召回返回条数上限，默认 20
  SearchFilter filter = 5;     // 可选过滤条件
  int32 page_size = 6;         // 每页条数，默认 50，最大 100
  string page_token = 7;       // 游标，首页传空
}

// 检索响应
message SearchResponse {
  repeated SearchResult results = 1;
  string next_page_token = 2;  // 游标，为空时表示已到最后一页
  int32 total_count = 3;       // 总命中数
}

// 单条检索结果
message SearchResult {
  string document_name = 1;    // 文档名
  int32 page_number = 2;       // 页码
  string associated_text = 3;  // 关联文本（教材类=句子，试卷类=题目文本）
  double score = 4;            // 相关度评分（0~1，越高越相关）
  string match_type = 5;       // 匹配类型：exact_phrase / exact_word / stem_match / semantic
  string segment_id = 6;       // 段落锚点 ID（用于高亮定位）
  int32 start_offset = 7;      // 命中文本在关联文本中的起始偏移
  int32 end_offset = 8;        // 命中文本在关联文本中的结束偏移
}

// gRPC 服务定义（仅查询能力）
service DocumentRetrievalService {
  // 精确检索：基于倒排索引的单词/短语匹配
  rpc ExactSearch(ExactSearchRequest) returns (SearchResponse);
  // 混合检索：精确匹配 + 语义召回
  rpc HybridSearch(HybridSearchRequest) returns (SearchResponse);
}
```

**字段说明**：

| 字段 | 约束 | 说明 |
|------|------|------|
| `query` | 必填，1~200 字符 | 传入空值或超长返回 `InvalidArgument` |
| `phrase` | — | `true` 时使用 `match_phrase`，保证短语不被拆碎；`false` 时使用 `match`，支持词形归一 |
| `exact_top_k` | 默认 50，最大 200 | 控制精确检索内部召回量，非最终返回条数 |
| `semantic_top_k` | 默认 20，最大 100 | 控制语义召回内部召回量，非最终返回条数 |
| `page_size` | 默认 50，最大 100 | 超过 100 返回 `InvalidArgument` |
| `page_token` | — | 首页传空字符串；后续页传上一次响应的 `next_page_token` |
| `match_type` | — | 标识命中来源，便于调用方区分精确命中与语义召回 |
| `start_offset` / `end_offset` | — | 命中词/短语在 `associated_text` 中的字符偏移，用于前端高亮 |

**游标分页实现**：

- `page_token` 编码为 Base64 字符串，内容为 `{sort_key, document_id, segment_id}` 的 JSON
- 排序键为 `score` 降序 + `document_id` 升序 + `segment_id` 升序
- 翻页时使用 `search_after` 语义，保证跨页一致性

### 5.4 删除语义

- Web 管理界面调用删除端点时，按 `title` 找到对应 `Document`，执行物理删除（DELETE）。
- 同步从 OpenSearch 与向量库中按 `document_id` 过滤删除全部条目。
- 删除完成后释放 `title` 唯一约束（同名可重新上传）。
- 删除过程中 gRPC 查询接口不受影响，**已就绪**的文档仍可正常被查询。
- 删除是**幂等**的：重复删除同一 `title`（文档已不存在）返回成功。

### 5.5 错误代码表

> 遵循 [统一错误处理规范](../../error-handling.md)，gRPC 错误信息使用中文。

| 错误代码 | gRPC StatusCode | HTTP 状态码 | 说明 |
|---------|-----------------|------------|------|
| `DOCRETRIEVAL_QUERY_REQUIRED` | `InvalidArgument` | 400 | 查询词不能为空 |
| `DOCRETRIEVAL_QUERY_TOO_LONG` | `InvalidArgument` | 400 | 查询词超过 200 字符 |
| `DOCRETRIEVAL_PAGE_SIZE_INVALID` | `InvalidArgument` | 400 | page_size 超过最大值 100 |
| `DOCRETRIEVAL_DOCUMENT_NOT_FOUND` | `NotFound` | 404 | 文档不存在 |
| `DOCRETRIEVAL_INGESTION_JOB_NOT_FOUND` | `NotFound` | 404 | 导入任务不存在 |
| `DOCRETRIEVAL_TITLE_ALREADY_EXISTS` | `AlreadyExists` | 409 | 文档名已存在 |
| `DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS` | `AlreadyExists` | 409 | 该文件已被导入 |
| `DOCRETRIEVAL_FILE_ENCRYPTED` | `InvalidArgument` | 400 | 不支持加密文件 |
| `DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED` | `InvalidArgument` | 400 | 不支持的文件格式 |
| `DOCRETRIEVAL_METADATA_REQUIRED` | `InvalidArgument` | 400 | 必填元数据缺失（title/subject/grade/year） |
| `DOCRETRIEVAL_SUBJECT_INVALID` | `InvalidArgument` | 400 | 学科取值非法（当前仅支持"英语"） |
| `DOCRETRIEVAL_GRADE_INVALID` | `InvalidArgument` | 400 | 年级取值非法（须在 K-12 范围内） |
| `DOCRETRIEVAL_DOCUMENT_NOT_READY` | `FailedPrecondition` | 422 | 文档未就绪，不允许修改元数据 |
| `DOCRETRIEVAL_IMMUTABLE_FIELD` | `InvalidArgument` | 400 | 不可变字段（文档名/文件哈希）不允许修改 |
| `DOCRETRIEVAL_INGESTION_FAILED` | `Internal` | 500 | 文档导入失败（解析/索引错误） |
| `DOCRETRIEVAL_INDEX_CLEANUP_FAILED` | `Internal` | 500 | 索引清理失败 |
| `DOCRETRIEVAL_OPENSEARCH_UNAVAILABLE` | `Unavailable` | 502 | OpenSearch 服务不可用 |
| `DOCRETRIEVAL_QDRANT_UNAVAILABLE` | `Unavailable` | 502 | Qdrant 服务不可用 |
| `DOCRETRIEVAL_EMBEDDING_FAILED` | `Internal` | 500 | Embedding 调用失败 |

---

## 6. 技术选型

### 6.1 确定工具链

> 以下选型均已通过 ADR 确认，详见 [adr/](./adr/) 目录。

| 层 | 工具 | 说明 |
|----|------|------|
| PDF 抽取 | `PDFPig`（首选）/ `iTextSharp` | .NET 原生，支持页码/段落/行级位置 |
| Word / PPT 抽取 | `OpenXML SDK` | .NET 原生，微软官方库 |
| 扫描件 OCR | `Tesseract.NET` | .NET 封装，英文识别率可接受 |
| 精确检索 | `OpenSearch` | 全文检索 + BM25 + 高亮 + 过滤 |
| 语义检索 | `Qdrant` | 专用向量库，过滤+向量组合检索 |
| Embedding | 外部 Embedding API（SiliconFlow） | .NET 调用 REST API，不依赖本地推理 |
| gRPC 服务 | .NET 8 + ASP.NET Core gRPC | 与现有微服务一致 |
| Web 管理界面 | ASP.NET Core Razor Pages | 轻量方案 |
| 数据库 | PostgreSQL | 与现有系统一致 |
| 对象存储 | SeaweedFS | 与现有系统一致，详见 [ADR 0004](./adr/0004-document-storage.md) |

### 6.2 明确不采用

- ❌ "只有聊天框、没有精确页码/题号返回"的伪 RAG
- ❌ "只上向量库就宣称支持检索定位"
- ❌ 把文档检索硬塞进现有背单词服务 / 错题服务
- ❌ 数据库 `LIKE` 作为主检索方案

### 6.3 工程偏差提示

已决定采用**全 .NET 方案**（详见 [ADR 0002](./adr/0002-language-stack.md)）。以下为历史备选方案记录，仅供参考：

- ~~**A. 混合**~~：.NET 做 gRPC 网关 + 编排层 + Web 管理界面，把 `DocumentParser` / `OcrEngine` / `Indexer` 拆成独立 Python 进程。部署复杂度高，已否决。
- ~~**B. 全 Python**~~：服务整体 Python（FastAPI / gRPC Python），与现有 .NET 风格不一致，已否决。
- **C. 全 .NET** ✅：PDFPig / iTextSharp + OpenXML SDK + Tesseract.NET，英文场景可胜任。

> 如未来扫描件质量极差或需要端到端模型推理，可独立抽离 Python 解析服务，但当前场景不满足这些条件。

---

## 7. 实施路径

### 7.1 阶段 A：核心链路验证（约 2 周）

**目标**：

- 验证真实教材 / 真题能否稳定抽取
- 验证扫描件 OCR 质量
- 验证"查词 → 返回句子/题目"最小闭环

**做法**：

- 选 20~50 份代表性文档，覆盖：
  - 电子版教材
  - Word 文档
  - 扫描试卷
- `PDFPig + OpenXML SDK + Tesseract.NET` 产出结构化 JSON
- `OpenSearch` 建立精确检索索引
- `Qdrant` 建立语义检索试验链路

**阶段验收**：

- ✅ 输入单词 → 返回命中文档、页码、句子
- ✅ 输入短语 → 返回完整短语命中（不拆碎）
- ✅ 对扫描件 → 在可接受误差内返回对应题目文本

### 7.2 阶段 B：业务化最小产品

**目标**：

- 做成正式服务接口
- 接入当前微服务体系
- 提供 Web 管理界面

**做法**：

- 新增独立 `ruoyu.docretrieval` 服务
- 暴露 gRPC 接口：精确搜索、混合搜索
- 内置 Web 管理界面：文档上传、导入状态查看、文档删除
- 搜索结果作为独立能力对外提供，上层业务按需消费

### 7.3 阶段 C：能力增强

- 优化句子级 / 题目级切分策略
- 优化 OCR 错字纠偏与题号识别
- 优化混合排序与高亮展示

---

## 8. 验收标准

### 8.1 功能验收

- 输入单词：返回出现的文档、页码、所在句/题
- 输入短语：返回精确短语命中 + 上下文 + 必要时补充语义相近题目
- 扫描件：返回可读、可定位的结果（非模糊摘要）
- 删除文档：按 `title` 硬删除成功，gRPC 查询结果中该文档不再出现
- Web 管理界面：能正常上传 / 删除 / 查看导入状态

### 8.2 指标建议

| 指标 | 目标 |
|------|------|
| 精确词命中准确率 | 优先高（≥ 行业可比基线） |
| 精确短语命中准确率 | 明显高于普通 chunk RAG |
| 扫描件题目定位成功率 | 达到可用线 |
| 精确检索耗时 | 秒级内 |
| 语义扩展耗时 | 略慢于精确检索 |
| 删除接口耗时 | 秒级内（同步） |

### 8.3 阶段 A 验收口径

- 准备 3 组样本：电子教材 PDF、Word 真题、扫描试卷 PDF
- 每组样本执行：文本抽取成功率、OCR 可读率、页码/段落/题号锚点、精确词查询、精确短语查询、语义近似查询

---

## 9. 决策记录

### 9.1 已确认

- ✅ 文档类型同时支持原生文本和扫描件
- ✅ 同时支持精确定位与语义扩展
- ✅ 当前仓库无 RAG 基建，需自建
- ✅ 中规模数据精确检索优先选搜索引擎，向量库不作主检索引擎
- ✅ 业务系统只能调用查询接口，上传 / 删除通过 Web 管理界面操作
- ✅ 文档名唯一（重复拒绝），删除为硬删除（释放文档名）

### 9.2 核心设计原则

- 主方案：`OCR/解析 + OpenSearch + 向量库 + 自建编排服务 + Web 管理界面`
- 锚点模型优先级 > 聊天问答体验
- 返回结果以"位置可解释"为第一原则

### 9.3 已决策（原待决策项，均已通过 ADR 确认）

- ✅ 语言栈：**全 .NET** — [ADR 0002](./adr/0002-language-stack.md)
- ✅ 向量库：**Qdrant** — [ADR 0003](./adr/0003-vector-database.md)
- ✅ 文档存储：**复用 SeaweedFS** — [ADR 0004](./adr/0004-document-storage.md)

---

## 10. 参考文档

- [业务需求文档](./requirements.md) — 业务目标、服务调用方、业务验收口径
- [业务流程文档](./process.md) — 参与者、流程、状态机、异常分支
- [讨论计划原文](../../../plan/english-doc-rag-scheme-plan.md) — 选型背景与原始讨论
- [验收清单](./checklist.md) — 阶段 A/B/C 的验收项
- [实施任务列表](./tasks.md) — 阶段 A 任务拆解
- [系统架构文档](../../architecture.md) — 微服务体系全局视角
- [部署规格文档](../../deployment.md) — 部署模式参考
- [数据库规格文档](../../database-spec.md) — 数据库命名与迁移规范
- [统一错误处理规范](../../error-handling.md) — gRPC 错误规范
