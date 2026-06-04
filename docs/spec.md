# DocRetrieval 技术规格说明

> 本文档为 `ruoyu.docretrieval` 服务的正式技术规格（Spec），**只描述"怎么做"**。
> 业务需求（做什么、为谁做、做到什么程度）见 [requirements.md](./requirements.md)；
> 业务流程（谁在什么时机做什么、状态如何流转）见 [process.md](./process.md)。
>
> 配套文档：[checklist.md](./checklist.md)（验收清单）、[tasks.md](./tasks.md)（实施任务）、[adr/](./adr/)（架构决策记录）。

## 1. 概述

### 1.1 服务定位

- 服务目录：`backend/ruoyu.docretrieval/`
- 服务命名空间：`Ruoyu.Study.DocRetrieval`
- 解决方案：`Ruoyu.Study.DocRetrieval.sln`
- 定位：独立的**文档检索域**微服务，自带 Web 管理界面用于文档管理，对外通过 gRPC 暴露查询定位能力。

### 1.2 技术职责

- 接收英文教材与考试真题的 PDF（含扫描件）/ Word / PPT
- 解析出"文档展示锚点"作为一等数据
- 提供**精确检索**与**语义扩展**两类检索能力
- 自带 Web 管理界面用于文档上传 / 删除 / 查看导入状态
- 对外（业务系统）仅通过 gRPC 暴露查询定位接口

**不在本服务范围**：聊天式问答、错题 / 作业业务、单词释义、账号与权限。

### 1.3 术语表

| 术语 | 含义 |
|------|------|
| 文档（Document） | 一份 PDF（含扫描件）/ Word / PPT，对应一个导入任务 |
| 锚点（Anchor） | 文档内可定位的最小单位（页/块/句/题） |
| 精确检索 | 基于倒排索引的单词/短语匹配 |
| 语义召回 | 基于向量相似度的近义表达召回 |
| 混合检索 | 同时返回精确匹配与语义匹配结果 |
| 硬删除 | 物理删除文档相关数据（元数据、文件、任务记录、检索索引），释放文档名，不可恢复 |

---

## 2. 技术架构决策

以下决策详见 [adr/](./adr/) 目录：

| 决策项 | 结论 | ADR |
|--------|------|-----|
| 架构决策记录机制 | 使用 ADR 格式，存储于 `docs/adr/` | [0001](./adr/0001-record-architecture-decisions.md) |
| 语言栈 | 全 .NET（.NET 8 + ASP.NET Core gRPC） | [0002](./adr/0002-language-stack.md) |
| 向量库 | Qdrant | [0003](./adr/0003-vector-database.md) |
| 文档存储 | 复用现有 SeaweedFS | [0004](./adr/0004-document-storage.md) |

---

## 3. 整体架构

### 3.1 分层设计

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
│  1. 文档解析层  (.NET 原生)                          │
│  - PDF: PDFPig / iText                               │
│  - Word: OpenXML                                     │
│  - PPT: OpenXML                                      │
│  - OCR: Tesseract.NET                                │
└──────────────────────────────────────────────────────┘
```

### 3.2 技术栈总览

| 层 | 技术选型 | 备注 |
|----|----------|------|
| 主语言 | .NET 8 + ASP.NET Core | 全 .NET 方案 |
| PDF 解析 | PDFPig / iText | .NET 生态 |
| Word/PPT 解析 | OpenXML SDK | .NET 生态 |
| OCR | Tesseract.NET | 英文场景足够 |
| 精确检索 | OpenSearch | 倒排索引 + 高亮 + 短语查询 |
| 语义检索 | Qdrant | 向量检索 + 混合过滤 |
| Embedding | 外部模型服务（如 Ollama） | 通过 HTTP/gRPC 调用 |
| 关系数据库 | PostgreSQL + EF Core | 元数据 + 任务状态 |
| 文档存储 | SeaweedFS | 复用现有基础设施 |
| 消息队列 | 待定（PostgreSQL 轮询 / Redis Stream） | 导入任务异步化 |

### 3.3 明确不采用

- ❌ "只有聊天框、没有精确页码/题号返回"的伪 RAG
- ❌ "只上向量库就宣称支持检索定位"
- ❌ 把文档检索硬塞进现有背单词服务 / 错题服务
- ❌ 数据库 `LIKE` 作为主检索方案
- ❌ Python 工具链（Docling / MinerU / PaddleOCR）

---

## 4. 文档解析层

### 4.1 输入格式

| 格式 | 后缀 | 解析方式 |
|------|------|----------|
| PDF（电子版） | `.pdf` | PDFPig / iText 直接抽取文本 |
| PDF（扫描件） | `.pdf` | Tesseract.NET OCR 识别 |
| Word | `.doc` / `.docx` | OpenXML SDK |
| PPT | `.ppt` / `.pptx` | OpenXML SDK |

### 4.2 职责

- 抽取原始文本
- 保留页码、段落、块、行级位置信息
- 识别句子边界
- 对试卷识别题目边界（题号、题干、选项、答案区域）
- 对扫描件执行 OCR

### 4.3 结构化产物

解析器产出统一 JSON 结构，供下游索引层消费：

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

### 4.4 关键原则

- "文档展示锚点"是一等数据，不只存 chunk 文本
- 解析过程是异步的，Web 管理界面上传后立即返回导入任务 ID
- 解析失败不阻塞服务，任务状态标记为 `失败` 并记录原因

---

## 5. 精确检索层（OpenSearch）

### 5.1 职责

- 精确搜索单词
- 精确搜索短语（不拆碎）
- 大小写归一、词形归一
- 返回页码、段落、句子、题号
- 高亮命中范围

### 5.2 为什么选 OpenSearch

- 对中规模文档库（数千份，单文档可上千页）更稳
- 原生支持全文检索、高亮、短语查询、BM25、过滤条件组合
- 比向量库更适合"查出现位置"
- 后续"按学科/年级/题型/年份"过滤更自然

### 5.3 索引 Mapping（草案）

```json
{
  "mappings": {
    "properties": {
      "document_id": { "type": "keyword" },
      "document_name": { "type": "keyword" },
      "page": { "type": "integer" },
      "block_id": { "type": "keyword" },
      "sentence_id": { "type": "keyword" },
      "question_id": { "type": "keyword" },
      "text": {
        "type": "text",
        "analyzer": "english",
        "fields": {
          "phrase": { "type": "text", "analyzer": "standard" }
        }
      },
      "subject": { "type": "keyword" },
      "grade": { "type": "keyword" },
      "year": { "type": "integer" },
      "segment_type": { "type": "keyword" }
    }
  }
}
```

---

## 6. 语义召回层（Qdrant）

### 6.1 职责

- 查相近表达
- 查意思相近的句子/题目
- 查某词在不同题型中的变体考法

### 6.2 切分粒度

- 句子级
- 题目级
- 必要时段落级

**禁止**：只做粗粒度 chunk（无法满足"返回哪一句/哪一道题"）。

### 6.3 向量存储结构

每个向量条目需携带过滤字段：

| 字段 | 类型 | 用途 |
|------|------|------|
| `document_id` | keyword | 按文档过滤 / 删除清理 |
| `document_name` | keyword | 按文档名过滤 |
| `subject` | keyword | 学科过滤 |
| `grade` | keyword | 年级过滤 |
| `year` | integer | 年份过滤 |
| `segment_type` | keyword | 区分句子 / 题目 |
| `text` | text | 原始文本 |
| `page` | integer | 页码 |
| `sentence_id` / `question_id` | keyword | 锚点 ID |

### 6.4 Embedding 模型

- 通过外部模型服务（如 Ollama）提供
- .NET 服务通过 HTTP 调用模型服务获取向量
- 选择英文表现优秀的模型（如 `nomic-embed-text`、`mxbai-embed-large`）

---

## 7. 数据模型

### 7.1 PostgreSQL 核心实体

| 实体 | 作用 | 关键字段 |
|------|------|----------|
| `Document` | 文档元数据 | id, title (unique), source_type, language, grade, subject, year, status, created_at |
| `DocumentPage` | 页面级锚点 | id, document_id, page_number, image_path |
| `DocumentSegment` | 句子/段落级锚点 | id, document_id, page_id, block_id, sentence_id, text, start_offset, end_offset |
| `QuestionSegment` | 题目锚点 | id, document_id, page_id, question_id, stem, options_json, answer_area |
| `DocumentIngestionJob` | 导入任务 | id, document_id, status, parser_version, ocr_version, error_message, started_at, finished_at |

### 7.2 实体关系

```
Document 1 ──* DocumentIngestionJob
Document 1 ──* DocumentPage
DocumentPage 1 ──* DocumentSegment
DocumentPage 1 ──* QuestionSegment
```

### 7.3 约束

- `Document.title` 有唯一索引（防止同名上传）
- 删除 `Document` 时级联删除关联的 Page、Segment、IngestionJob
- 数据库命名遵循 [database-spec.md](../../database-spec.md)

---

## 8. 接口设计

### 8.1 gRPC 服务清单（仅查询能力）

| RPC | 方法 | 用途 | 调用方 |
|-----|------|------|--------|
| `ExactSearch` | `POST` 风格 | 精确词/短语检索 | 业务系统 |
| `HybridSearch` | `POST` 风格 | 混合检索（精确 + 语义） | 业务系统 |

### 8.2 Web 管理接口（HTTP）

| 端点 | 方法 | 用途 |
|------|------|------|
| `/admin/documents/upload` | `POST` (multipart) | 上传文档（必填：title / subject / grade / year） |
| `/admin/documents` | `GET` | 查看已导入文档列表 |
| `/admin/documents/{id}/status` | `GET` | 查看导入任务状态 |
| `/admin/documents/{title}` | `DELETE` | 删除文档（硬删除，按文档名） |

**文档列表查询参数**：

| 参数 | 类型 | 说明 |
|------|------|------|
| `keyword` | query string | 按 `title` 模糊搜索 |
| `subject` | query string | 按学科精确筛选 |
| `grade` | query string | 按年级精确筛选 |
| `year` | query string | 按年份精确筛选 |
| `status` | query string | 按导入状态筛选（pending / processing / success / failed） |
| `page` | query string | 页码（从 1 开始） |
| `pageSize` | query string | 每页条数（默认 20） |

### 8.3 gRPC 消息定义（草案）

```protobuf
service DocumentRetrievalService {
  rpc ExactSearch(ExactSearchRequest) returns (SearchResponse);
  rpc HybridSearch(HybridSearchRequest) returns (SearchResponse);
}

message ExactSearchRequest {
  string query = 1;
  bool phrase = 2;                     // 是否短语查询
  repeated string filters = 3;         // 学科/年级/年份等过滤
  string document_name = 4;            // 按文档名过滤
  int32 page_size = 5;                 // 默认 50，最大 100
  string page_token = 6;               // 游标
}

message HybridSearchRequest {
  string query = 1;
  int32 exact_top_k = 2;
  int32 semantic_top_k = 3;
  repeated string filters = 4;         // 学科/年级/年份等过滤
  string document_name = 5;            // 按文档名过滤
  int32 page_size = 6;                 // 默认 50，最大 100
  string page_token = 7;               // 游标
}

message SearchResponse {
  repeated SearchResult results = 1;
  string next_page_token = 2;          // 游标，为空时表示已到最后一页
  int32 total_count = 3;               // 总命中数
}

message SearchResult {
  string document_name = 1;
  int32 page_number = 2;
  string associated_text = 3;          // 句子或题目文本
  double score = 4;                    // 相关度评分
}
```

### 8.4 删除语义

- 按 `title`（文档名）找到对应 `Document`，执行物理删除（DELETE）
- 同步从 OpenSearch 与 Qdrant 中按 `document_id` 过滤删除全部条目
- 同步从 SeaweedFS 中删除原始文件
- 删除完成后释放 `title` 唯一约束（同名可重新上传）
- 删除是**同步操作**，不引入任务 ID 跟踪
- 删除是**幂等**的：文档已不存在时返回成功
- 删除过程中 gRPC 查询接口不受影响

---

## 9. 项目结构

### 9.1 目录规划

```
backend/ruoyu.docretrieval/
├── Ruoyu.Study.DocRetrieval.sln
├── docs/
│   ├── requirements.md          # 业务需求
│   ├── process.md               # 业务流程
│   ├── spec.md                  # 技术规格（本文）
│   ├── checklist.md             # 验收清单
│   ├── tasks.md                 # 实施任务列表
│   └── adr/                     # 架构决策记录
│       ├── README.md
│       ├── 0001-record-architecture-decisions.md
│       ├── 0002-language-stack.md
│       ├── 0003-vector-database.md
│       └── 0004-document-storage.md
├── src/
│   ├── Contract/                # gRPC proto 定义
│   │   └── Ruoyu.Study.DocRetrieval.Contract.csproj
│   ├── Database/                # EF Core 实体 + 迁移
│   │   └── Ruoyu.Study.DocRetrieval.Database.csproj
│   ├── Domain/                  # 领域逻辑 + 解析器接口 + 检索编排
│   │   └── Ruoyu.Study.DocRetrieval.Domain.csproj
│   ├── Service/                 # gRPC 服务实现
│   │   └── Ruoyu.Study.DocRetrieval.Service.csproj
│   ├── WebAdmin/                # Web 管理界面
│   │   └── Ruoyu.Study.DocRetrieval.WebAdmin.csproj
│   └── Host/                    # 启动入口 + 配置
│       └── Ruoyu.Study.DocRetrieval.Host.csproj
└── test/
    └── Ruoyu.Study.DocRetrieval.Tests.csproj
```

---

## 10. 实施路径

### 10.1 阶段 A：核心链路验证（约 2 周）

**目标**：
- 验证真实教材 / 真题能否稳定抽取
- 验证扫描件 OCR 质量
- 验证"查词 → 返回句子/题目"最小闭环

**做法**：
- 选 20~50 份代表性文档，覆盖电子版教材、Word 文档、扫描试卷
- 使用 .NET 工具链（PDFPig/OpenXML/Tesseract.NET）产出结构化 JSON
- OpenSearch 建立精确检索索引
- Qdrant 建立语义检索试验链路

**阶段验收**：
- ✅ 输入单词 → 返回命中文档、页码、句子
- ✅ 输入短语 → 返回完整短语命中（不拆碎）
- ✅ 对扫描件 → 在可接受误差内返回对应题目文本

### 10.2 阶段 B：业务化最小产品

**目标**：
- 做成正式服务接口
- 接入当前微服务体系
- 提供 Web 管理界面

**做法**：
- 新增独立 `ruoyu.docretrieval` 服务（全 .NET）
- 暴露 gRPC 接口：精确搜索、混合搜索
- 内置 Web 管理界面：文档上传、导入状态查看、文档删除
- 搜索结果作为独立能力对外提供

### 10.3 阶段 C：能力增强

- 优化句子级 / 题目级切分策略
- 优化 OCR 错字纠偏与题号识别
- 优化混合排序与高亮展示

---

## 11. 验收标准

### 11.1 功能验收

- 输入单词：返回出现的文档名、页码、所在句/题
- 输入短语：返回精确短语命中 + 上下文 + 必要时补充语义相近题目
- 扫描件：返回可读、可定位的结果（非模糊摘要）
- 删除文档：按文档名硬删除成功，gRPC 查询结果中该文档不再出现
- Web 管理界面：能正常上传 / 删除 / 查看导入状态

### 11.2 指标建议

| 指标 | 目标 |
|------|------|
| 精确词命中准确率 | 优先高（≥ 行业可比基线） |
| 精确短语命中准确率 | 明显高于普通 chunk RAG |
| 扫描件题目定位成功率 | 达到可用线 |
| 精确检索耗时 | 秒级内 |
| 语义扩展耗时 | 略慢于精确检索 |
| 删除接口耗时 | 秒级内（同步） |

### 11.3 阶段 A 验收口径

- 准备 3 组样本：电子教材 PDF、Word 真题、扫描试卷 PDF
- 每组样本执行：文本抽取成功率、OCR 可读率、页码/段落/题号锚点、精确词查询、精确短语查询、语义近似查询

---

## 12. 参考文档

- [业务需求文档](./requirements.md) — 业务目标、服务调用方、业务验收口径
- [业务流程文档](./process.md) — 参与者、流程、状态机、异常分支
- [验收清单](./checklist.md) — 阶段 A/B/C 的验收项
- [实施任务列表](./tasks.md) — 阶段 A 任务拆解
- [架构决策记录](./adr/) — 关键技术决策
- [系统架构文档](../../architecture.md) — 微服务体系全局视角
- [部署规格文档](../../deployment.md) — 部署模式参考
- [数据库规格文档](../../database-spec.md) — 数据库命名与迁移规范
- [统一错误处理规范](../../error-handling.md) — gRPC 错误规范
