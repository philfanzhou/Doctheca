# DocRetrieval 实施任务列表

> 配套文档：[requirements.md](./requirements.md)（业务需求）｜[process.md](./process.md)（业务流程）｜[spec.md](./spec.md)（技术规格）｜[checklist.md](./checklist.md)（验收清单）
>
> 用法：每条任务在开工时切换为 `[-]`，完成后切换为 `[x]`；阶段切换时整体打包评审。
>
> **业务变更**：如果某条任务"业务上不再需要 / 范围调整"，应先更新 [requirements.md](./requirements.md)，再回到本文件调整任务。
> **技术变更**：如果某条任务"实现方式调整"，应先更新 [spec.md](./spec.md)，再回到本文件调整任务。

## 阶段 A：核心链路验证（约 2 周）

### A0. 启动前置

- [ ] **A0.1** 在 [系统架构文档](../../architecture.md) 登记服务占位
- [ ] **A0.2** 在 docker-compose 中占位 OpenSearch / Qdrant

### A1. 样本准备

- [ ] **A1.1** 收集至少 20 份电子教材 PDF
- [ ] **A1.2** 收集至少 10 份 Word 真题
- [ ] **A1.3** 收集至少 10 份扫描试卷 PDF
- [ ] **A1.4** 编写样本登记脚本（记录来源、年级、学科、年份）

### A2. 文档解析层（.NET POC）

- [ ] **A2.1** 集成 PDFPig 或 iText，抽取 PDF 原生文本
- [ ] **A2.2** 验证抽取结果含页码、段落、块、行级位置
- [ ] **A2.3** 集成 OpenXML SDK，解析 Word / PPT
- [ ] **A2.4** 集成 Tesseract.NET，处理扫描件 OCR
- [ ] **A2.5** 定义结构化 JSON Schema（含 `tokens` 字段）
- [ ] **A2.6** 实现句子边界识别
- [ ] **A2.7** 实现试卷题目边界识别（题号、题干、选项、答案区域）
- [ ] **A2.8** 写一个最小 CLI：`dotnet run -- ingest <file> --out <json>`
- [ ] **A2.9** 抽取成功率自检脚本（电子版 / Word / 扫描件分别统计）

### A3. 精确检索层（OpenSearch POC）

- [ ] **A3.1** 启动本地 OpenSearch
- [ ] **A3.2** 设计索引 Mapping（含 `document_id / page / block_id / sentence_id / question_id / text` 与高亮配置）
- [ ] **A3.3** 编写索引写入程序（消费 A2 的结构化 JSON）
- [ ] **A3.4** 实现单词查询
- [ ] **A3.5** 实现短语查询
- [ ] **A3.6** 实现大小写归一 + 词形归一
- [ ] **A3.7** 实现命中结果高亮偏移
- [ ] **A3.8** 编写最小查询 CLI：`dotnet run -- search "encourage"`

### A4. 语义召回层（Qdrant POC）

- [ ] **A4.1** 启动本地 Qdrant
- [ ] **A4.2** 部署/接入 Embedding 模型服务（如 Ollama）
- [ ] **A4.3** 实现句子级切分
- [ ] **A4.4** 实现题目级切分
- [ ] **A4.5** 实现 Embedding 写入（.NET 调用模型服务 → 写入 Qdrant）
- [ ] **A4.6** 实现语义相似查询
- [ ] **A4.7** 验证返回结果带锚点（不是纯 chunk）

### A5. 阶段 A 验收

- [ ] **A5.1** 端到端脚本：单词 → 命中文档 / 页码 / 句
- [ ] **A5.2** 端到端脚本：短语 → 完整短语命中
- [ ] **A5.3** 端到端脚本：扫描件 → 题目定位
- [ ] **A5.4** 撰写阶段 A 验收报告 → `docs/verification/phase-a.md`
- [ ] **A5.5** 阶段 A 评审通过后才能进入阶段 B

---

## 阶段 B：业务化最小产品

### B1. 服务骨架

- [ ] **B1.1** 创建 `backend/ruoyu.docretrieval/` 目录
- [ ] **B1.2** 创建 `Ruoyu.Study.DocRetrieval.sln`
- [ ] **B1.3** 创建 `src/Contract/Ruoyu.Study.DocRetrieval.Contract.csproj` 与 `*.proto`
- [ ] **B1.4** 创建 `src/Database/Ruoyu.Study.DocRetrieval.Database.csproj`
- [ ] **B1.5** 创建 `src/Domain/Ruoyu.Study.DocRetrieval.Domain.csproj`
- [ ] **B1.6** 创建 `src/Service/Ruoyu.Study.DocRetrieval.Service.csproj`
- [ ] **B1.7** 创建 `src/Host/Ruoyu.Study.DocRetrieval.Host.csproj`（含 `Program.cs` + `appsettings.json`）
- [ ] **B1.8** 创建 `src/WebAdmin/Ruoyu.Study.DocRetrieval.WebAdmin.csproj`（Web 管理界面项目）
- [ ] **B1.9** 创建 `test/Ruoyu.Study.DocRetrieval.Tests.csproj`
- [ ] **B1.10** 将服务登记到 [系统架构文档](../../architecture.md) 的服务表

### B2. 数据模型与迁移

- [ ] **B2.1** 实现 `Document` 实体 + EF Core 配置（含唯一约束 `title`）
- [ ] **B2.2** 实现 `DocumentPage` 实体
- [ ] **B2.3** 实现 `DocumentSegment` 实体
- [ ] **B2.4** 实现 `QuestionSegment` 实体
- [ ] **B2.5** 实现 `DocumentIngestionJob` 实体
- [ ] **B2.6** 生成并提交 EF Core 初始迁移
- [ ] **B2.7** 数据库命名遵循 [database-spec.md](../../database-spec.md)
- [ ] **B2.8** 为 `Document.title` 添加唯一索引（防重名）

### B3. gRPC 查询接口实现（仅对外暴露查询能力）

- [ ] **B3.1** 定义 `DocumentRetrievalService` proto（仅 `ExactSearch` + `HybridSearch`，含游标分页参数）
- [ ] **B3.2** 实现 `ExactSearch`（含游标分页）
- [ ] **B3.3** 实现 `HybridSearch`（含游标分页）
- [ ] **B3.4** 游标分页：`page_size` 默认 50 / 最大 100 校验
- [ ] **B3.5** 错误处理遵循 [error-handling.md](../../error-handling.md)
- [ ] **B3.6** 加入 gRPC 拦截器（traceId、异常、限流）
- [ ] **B3.7** 确认业务系统无法通过 gRPC 调用上传 / 删除能力

### B4. Web 管理界面实现

- [ ] **B4.1** 实现 `POST /admin/documents/upload` 文档上传端点（multipart，必填 title / subject / grade / year，重名拒绝）
- [ ] **B4.2** 实现 `GET /admin/documents` 文档列表端点（支持分页 + keyword 模糊搜索 + subject/grade/year/status 筛选）
- [ ] **B4.3** 实现 `GET /admin/documents/{id}/status` 导入状态查看端点
- [ ] **B4.4** 实现 `DELETE /admin/documents/{title}` 文档删除端点（硬删除，按文档名）
- [ ] **B4.5** Web 界面 UI：上传页面（文件选择 + 必填元数据填写 + 重名提示）
- [ ] **B4.6** Web 界面 UI：文档列表（分页 + keyword 搜索框 + subject/grade/year 下拉筛选 + 状态筛选）
- [ ] **B4.7** Web 界面 UI：删除确认与结果提示
- [ ] **B4.8** 鉴权集成（由基础设施层网关 / 反向代理负责，本服务不自行实现用户态）

### B5. 解析与索引同步

- [ ] **B5.1** 实现文档解析器（PDFPig / OpenXML / Tesseract.NET 封装）
- [ ] **B5.2** 实现导入任务队列（PostgreSQL 轮询 / Redis Stream / RabbitMQ 任选）
- [ ] **B5.3** 解析 Worker 消费任务 → 写结构化数据
- [ ] **B5.4** 索引同步器：结构化数据 → OpenSearch
- [ ] **B5.5** 索引同步器：结构化数据 → Qdrant（含 Embedding 调用）
- [ ] **B5.6** 失败重试 + 死信
- [ ] **B5.7** 删除清理：按 `document_id` 从 OpenSearch 与 Qdrant 移除条目，从 SeaweedFS 删除原始文件

### B6. 测试

- [ ] **B6.1** 单元测试：编排层聚合 / 排序逻辑
- [ ] **B6.2** 单元测试：实体映射 / 仓储
- [ ] **B6.3** 集成测试：gRPC 查询接口全量跑通
- [ ] **B6.4** 集成测试：导入 → 检索端到端
- [ ] **B6.5** 集成测试：导入 → 删除 → 查询不可见
- [ ] **B6.6** Web 管理界面上传 / 删除 / 状态查看功能测试

### B7. 部署

- [ ] **B7.1** `Host` 项目 Dockerfile（含 gRPC + Web Admin）
- [ ] **B7.2** docker-compose 新增服务条目
- [ ] **B7.3** 启动冒烟测试脚本（gRPC + Web Admin 均覆盖）
- [ ] **B7.4** 在 [deployment.md](../../deployment.md) 登记部署说明

---

## 阶段 C：能力增强

- [ ] **C.1** 句子级 / 题目级切分策略优化
- [ ] **C.2** OCR 错字纠偏（自定义词典 + 上下文纠错）
- [ ] **C.3** 题号识别准确率提升
- [ ] **C.4** 混合排序权重调优（A/B 测试）
- [ ] **C.5** 高亮展示优化（前端协同）
- [ ] **C.6** 性能压测报告（导入 / 检索 QPS / 延迟分布）
- [ ] **C.7** 容量规划与水平扩容策略

---

## 跨阶段持续项

- [ ] **X.1** 维护 [spec.md](./spec.md) 与实现一致
- [ ] **X.2** 维护 [checklist.md](./checklist.md) 验收记录
- [ ] **X.3** 提交前走 [pre-commit-check.md](../../../.testcode/docs/pre-commit-check.md) 4 阶段
- [ ] **X.4** 关键决策追加 ADR
- [ ] **X.5** 与上游服务（`ruoyu.student` / `ruoyu.questionBank`）保持接口兼容

---

## 任务状态总览

| 阶段 | 总任务 | 已完成 | 进行中 | 阻塞 |
|------|--------|--------|--------|------|
| 阶段 A | 35 | 0 | 0 | 0 |
| 阶段 B | 49 | 0 | 0 | 0 |
| 阶段 C | 7 | 0 | 0 | 0 |
| 跨阶段 | 5 | 0 | 0 | 0 |
| **合计** | **96** | **0** | **0** | **0** |

> 表格在阶段切换时手工更新。
