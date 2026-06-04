# DocRetrieval 验收清单

> 配套文档：[requirements.md](./requirements.md)（业务需求）｜[process.md](./process.md)（业务流程）｜[spec.md](./spec.md)（技术规格）｜[tasks.md](./tasks.md)（实施任务）
>
> 用法：每个阶段交付前，逐项勾选；未勾选项需在交付说明中显式标注 N/A 或延期原因。
>
> **业务验收口径**见 [requirements.md §6](./requirements.md#6-业务验收口径)；**技术指标**见本文档与 [spec.md §11](./spec.md#11-验收标准)。

## 0. 阶段 A 启动前置（已完成）

> 以下决策已在 ADR 中确认，阶段 A 可直接启动。

- [x] 语言栈决策已确认：全 .NET（[ADR 0002](./adr/0002-language-stack.md)）
- [x] 向量库选型已确认：Qdrant（[ADR 0003](./adr/0003-vector-database.md)）
- [x] 文档存储方案已确认：复用 SeaweedFS（[ADR 0004](./adr/0004-document-storage.md)）
- [ ] 服务端口与命名空间已登记到 [系统架构文档](../../architecture.md)
- [ ] 中间件依赖（OpenSearch / Qdrant）已在 docker-compose 中占位

---

## 1. 阶段 A：核心链路验证（2 周）

### 1.1 样本准备

- [ ] 至少 20 份电子教材 PDF 样本
- [ ] 至少 10 份 Word 真题样本
- [ ] 至少 10 份扫描试卷 PDF 样本
- [ ] 样本覆盖 3 种以上学科
- [ ] 样本含不同年份与年级
- [ ] 每组样本填写完整的必填元数据（title / subject / grade / year）
- [ ] 重名样本：验证同名上传被拒绝

### 1.2 文档解析层

- [ ] PDF 原生文本抽取成功率 ≥ 95%
- [ ] Word 抽取成功率 100%
- [ ] PPT 抽取成功率 100%
- [ ] 扫描件 OCR 可读率 ≥ 90%
- [ ] 抽取结果保留页码、段落、块、行级位置
- [ ] 句子边界识别准确
- [ ] 试卷类样本能识别题号、题干、选项、答案区域
- [ ] 结构化 JSON 产物含 `document_id / page / block_id / sentence_id / question_id / text / tokens`
- [ ] 锚点数据可被下游层无歧义消费

### 1.3 精确检索层

- [ ] 单词查询可返回命中文档、页码、句子
- [ ] 短语查询返回完整短语命中（非拆碎）
- [ ] 大小写归一生效
- [ ] 词形归一生效（study / studies / studied 可互查）
- [ ] 命中结果带高亮偏移
- [ ] 检索耗时 < 1s（中规模数据）

### 1.4 语义召回层

- [ ] 语义近似查询可返回意思相近的句子/题目
- [ ] 切分粒度为句子级 + 题目级
- [ ] 不存在"只有 chunk 没有锚点"的中间产物
- [ ] 检索耗时在可接受范围（可略慢于精确）

### 1.5 阶段 A 综合验收

- [ ] 端到端：输入单词 → 返回命中文档、页码、所在句/题
- [ ] 端到端：输入短语 → 返回精确短语命中 + 上下文
- [ ] 端到端：扫描件 → 返回可定位的题目文本
- [ ] 三类样本全部通过端到端测试
- [ ] 阶段 A 验收报告已归档到 `docs/verification/phase-a.md`

---

## 2. 阶段 B：业务化最小产品

### 2.1 服务骨架

- [ ] `backend/ruoyu.docretrieval/` 目录与 sln 文件就绪
- [ ] `src/Contract/` proto 文件定义 `DocumentRetrievalService`
- [ ] `src/Database/` 含 6 个核心实体（Document / DocumentPage / DocumentSegment / QuestionSegment / DocumentOccurrence / DocumentIngestionJob）
- [ ] `src/Domain/` 含检索编排与解析器接口
- [ ] `src/Host/` 启动服务可正常拉起
- [ ] `src/Service/` gRPC 实现可被 grpcurl 调通
- [ ] `test/` 含单元测试与集成测试
- [ ] `docs/` 含本套 spec / checklist / tasks 文档

### 2.2 gRPC 查询接口

- [ ] `ExactSearch` 接口实现并通过测试
- [ ] `HybridSearch` 接口实现并通过测试
- [ ] 业务系统只能调用查询接口（不能通过 gRPC 上传 / 删除）
- [ ] 游标分页生效：`page_size` + `page_token` 正确
- [ ] 默认 page_size = 50，最大 100；超过 100 返回错误
- [ ] `next_page_token` 为空时表示最后一页
- [ ] `total_count` 返回总命中数
- [ ] 所有错误响应遵循 [统一错误处理规范](../../error-handling.md)
- [ ] 错误信息使用中文

### 2.3 索引同步

- [ ] 文档导入后自动同步至 OpenSearch
- [ ] 文档导入后自动同步至向量库
- [ ] 失败任务可被重试而不污染索引
- [ ] 索引更新有可观测日志

### 2.4 Web 管理界面

- [ ] 上传端点可接收 PDF / Word / PPT 文件 + 必填元数据（title / subject / grade / year）
- [ ] 上传时必填元数据缺失 → 界面拦截
- [ ] 上传时文档名已存在 → 立即拒绝，提示"文档名已存在"
- [ ] 导入状态在界面可见，状态（进行中 / 成功 / 失败）实时更新
- [ ] 删除端点按 title 硬删除成功（物理删除元数据、文件、任务记录）
- [ ] 删除后 OpenSearch 中按 `document_id` 过滤的条目被清空
- [ ] 删除后向量库中按 `document_id` 过滤的条目被清空
- [ ] 删除后 PostgreSQL 中对应 `Document` 记录被物理删除（title 唯一约束释放）
- [ ] 删除是幂等的：重复删除同一 title（文档已不存在）返回成功
- [ ] 删除端点对 gRPC 查询接口无阻塞
- [ ] 已被硬删除的文档在 `ExactSearch` / `HybridSearch` 中不可见
- [ ] 文档列表端点支持分页（page / pageSize）
- [ ] 文档列表端点支持按 title 模糊搜索（keyword）
- [ ] 文档列表端点支持按 subject / grade / year / status 筛选

### 2.5 接入微服务

- [ ] 已在 [系统架构文档](../../architecture.md) 中登记服务端口与依赖
- [ ] 已与 `ruoyu.student` / `ruoyu.questionBank` 等至少 1 个上游服务做联调
- [ ] 服务能在 docker-compose 中独立拉起
- [ ] gRPC 查询端点可被 grpcurl 调通
- [ ] Web 管理界面可在浏览器正常访问

---

## 3. 阶段 C：能力增强

- [ ] 句子级 / 题目级切分策略优化完成
- [ ] OCR 错字纠偏上线
- [ ] 题号识别准确率提升
- [ ] 混合排序权重调优
- [ ] 高亮展示优化
- [ ] 性能压测报告归档

---

## 4. 通用质量项（所有阶段都需满足）

### 4.1 文档完整性

- [ ] `docs/README.md` 作为入口可正确导航
- [ ] `docs/spec.md` 与代码实现保持一致
- [ ] 公共 API 变更时同步更新 spec
- [ ] 关键决策有 ADR 记录

### 4.2 代码质量

- [ ] 通过 [pre-commit-check.md](../../../.testcode/docs/pre-commit-check.md) 全部 4 阶段
- [ ] .NET 项目遵循 [DotNetCodingPolicy.md](../../DotNetCodingPolicy.md)
- [ ] 单元测试覆盖核心域逻辑

### 4.3 可观测性

- [ ] gRPC 拦截器记录 traceId / spanId
- [ ] 关键指标暴露（导入成功率 / 检索耗时 / 召回率）
- [ ] 错误日志结构化

### 4.4 部署与运行

- [ ] 镜像可在 docker-compose 中启动
- [ ] 启动后端到端冒烟测试通过
- [ ] OpenSearch / Qdrant 等中间件有健康检查

---

## 5. 验收签字栏

| 阶段 | 验收人 | 日期 | 结论（通过/不通过） | 备注 |
|------|--------|------|--------------------|------|
| 阶段 A |        |      |                    |      |
| 阶段 B |        |      |                    |      |
| 阶段 C |        |      |                    |      |
