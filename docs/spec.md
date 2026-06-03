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

### 1.2 业务边界速览

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
| 主语言 | 现有后端为 .NET；方案工具链偏 Python（Docling/PaddleOCR） |
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
│  (OpenSearch)        │  │  (Qdrant / pgvector)     │
└──────────────────────┘  └─────────────────────────┘
            ↑                       ↑
┌───────────┴───────────────────────┴────────────────┐
│  1. 文档解析层  (Python 工具链)                      │
│  - Docling / MinerU / PaddleOCR                      │
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

**工具方向**：

- 原生抽取：`Docling` 或 `MinerU`（备选 `Unstructured`）
- OCR：`PaddleOCR`；若扫描件多，加 `OCRmyPDF` 预处理

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

### 3.4 第 3 层：语义召回层

**职责**：

- 查相近表达
- 查意思相近的句子/题目
- 查某词在不同题型中的变体考法

**推荐**：`Qdrant`（或短期 `pgvector`）

**选择建议**：

- 中规模、团队求简单 → 先 `PostgreSQL + pgvector`
- 语义检索会逐步扩张 → 直接 `Qdrant`

**切分粒度**：

- 句子级
- 题目级
- 必要时段落级

**禁止**：只做粗粒度 chunk（无法满足"返回哪一句/哪一道题"）。

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
| `Document` | 文档元数据 | id, title, source_type, language, grade, subject, year, status, created_at |
| `DocumentPage` | 页面级锚点 | id, document_id, page_number, image_path |
| `DocumentSegment` | 句子/段落级锚点 | id, document_id, page_id, block_id, sentence_id, text, start_offset, end_offset |
| `QuestionSegment` | 题目锚点 | id, document_id, page_id, question_id, stem, options_json, answer_area |
| `DocumentIngestionJob` | 导入任务 | id, document_id, status, parser_version, ocr_version, error_message, started_at, finished_at |

### 4.2 结构化锚点模型

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
| `/admin/documents/{id}/delete` | `POST` | 删除文档（硬删除） | Web 管理界面 |

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

### 5.3 gRPC 消息定义（草案）

```protobuf
service DocumentRetrievalService {
  rpc ExactSearch(ExactSearchRequest) returns (SearchResponse);
  rpc HybridSearch(HybridSearchRequest) returns (SearchResponse);
}

message ExactSearchRequest {
  string query = 1;
  bool phrase = 2;                     // 是否短语查询
  repeated string filters = 3;         // 教材/年级/学科/年份过滤
  int32 page_size = 4;                 // 默认 50，最大 100
  string page_token = 5;               // 游标
}

message HybridSearchRequest {
  string query = 1;
  int32 exact_top_k = 2;
  int32 semantic_top_k = 3;
  repeated string filters = 4;
  int32 page_size = 5;                 // 默认 50，最大 100
  string page_token = 6;               // 游标
}

message SearchResponse {
  repeated SearchResult results = 1;
  string next_page_token = 2;          // 游标，为空时表示已到最后一页
  int32 total_count = 3;               // 总命中数
}

message SearchResult {
  string document_name = 1;
  int32 page_number = 2;
  string associated_text = 3;          // 句子（教材类）或题目文本（试卷类）
  double score = 4;                    // 相关度评分
}
```

### 5.4 删除语义

- Web 管理界面调用删除端点时，按 `title` 找到对应 `Document`，执行物理删除（DELETE）。
- 同步从 OpenSearch 与向量库中按 `document_id` 过滤删除全部条目。
- 删除完成后释放 `title` 唯一约束（同名可重新上传）。
- 删除过程中 gRPC 查询接口不受影响，**已就绪**的文档仍可正常被查询。
- 删除是**幂等**的：重复删除同一 `title`（文档已不存在）返回成功。

---

## 6. 技术选型

### 6.1 推荐工具链

| 层 | 工具 | 备选 |
|----|------|------|
| 文档抽取 | `Docling` / `MinerU` | `Unstructured` |
| OCR | `PaddleOCR` | `OCRmyPDF`（预处理） |
| 精确检索 | `OpenSearch` | `Elasticsearch` |
| 语义检索 | `Qdrant` | `pgvector`（短期） |
| gRPC 服务 | .NET 8 + ASP.NET Core gRPC | — |
| Web 管理界面 | ASP.NET Core Razor Pages / Blazor Server | — |
| 数据库 | PostgreSQL | — |
| 对象存储 | SeaweedFS（与现有系统一致） | — |

### 6.2 明确不采用

- ❌ "只有聊天框、没有精确页码/题号返回"的伪 RAG
- ❌ "只上向量库就宣称支持检索定位"
- ❌ 把文档检索硬塞进现有背单词服务 / 错题服务
- ❌ 数据库 `LIKE` 作为主检索方案

### 6.3 工程偏差提示

方案中 `Docling / MinerU / PaddleOCR / Qdrant` 全部为 Python 生态，而现有 `backend/*` 均为 .NET。三种走法：

- **A. 混合（推荐）**：.NET 做 gRPC 网关 + 编排层 + Web 管理界面（`Host`/`Service`），把 `DocumentParser` / `OcrEngine` / `Indexer` 拆成独立 Python 进程，通过内部队列/接口解耦。业务系统只看到 .NET gRPC。
- **B. 全 Python**：服务整体 Python（FastAPI / gRPC Python），与现有 .NET 风格不一致。
- **C. 全 .NET**：换成 PDFPig / iText + Tesseract + Lucene.Net，OCR/版面解析能力会明显降级。

> ⚠️ 阶段 A 开始前必须先确认选 A / B / C，否则方案会被 Python 工具链拖动重构。

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
- `Docling/MinerU + OCR` 产出结构化 JSON
- `OpenSearch` 建立精确检索索引
- `Qdrant` 或 `pgvector` 建立语义检索试验链路

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

### 9.3 待决策（需在阶段 A 启动前敲定）

- ⏳ 语言栈：混合（.NET 编排 + Python 处理） / 全 Python / 全 .NET
- ⏳ 向量库：Qdrant / pgvector
- ⏳ 文档存储：是否复用现有 SeaweedFS / 还是单独对象存储

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
