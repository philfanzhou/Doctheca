# 5. 导入任务队列选型

## 状态

已决定 (Accepted)

## 上下文

DocRetrieval 服务的文档导入是异步流程：上传受理后，需要后台 Worker 消费任务进行解析、建索引。需要一个任务队列机制来：

1. 解耦上传端点与解析 Worker
2. 支持任务状态追踪
3. 支持失败重试
4. 支持取消（删除导入中的文档时需取消后续处理）

## 决策

选择 **PostgreSQL 轮询（基于 document_ingestion_jobs 表）** 作为任务队列方案。

选项：
1. ~~**PostgreSQL 轮询**~~ - 基于 `document_ingestion_jobs` 表状态轮询 ✅ 已选
2. **Redis Stream** - 利用现有 Redis 基础设施
3. **RabbitMQ** - 专业消息队列

## 备选方案分析

### 方案 A：PostgreSQL 轮询 ✅

- **优点**：
  - 无需新增中间件，利用已有的 PostgreSQL
  - 任务状态天然持久化，与 `document_ingestion_jobs` 表合一
  - 实现简单：Worker 定时查询 `status = 'pending'` 的任务
  - 删除文档时直接更新任务状态为 `cancelled`，Worker 下次轮询自动跳过
  - 与现有 `ruoyu.student` 的 `UploadAnalysisWorker` 模式一致
- **缺点**：
  - 轮询有延迟（可通过缩短间隔缓解，如 5 秒）
  - 高并发写入时可能有锁竞争（中规模场景不构成问题）

### 方案 B：Redis Stream

- **优点**：
  - 实时推送，无轮询延迟
  - 支持消费者组
  - 项目已有 Redis 基础设施
- **缺点**：
  - 任务状态需要双写（Redis Stream + PostgreSQL），增加一致性风险
  - 取消任务需要额外逻辑（从 Stream 中移除消息）
  - 引入新组件依赖，增加部署复杂度

### 方案 C：RabbitMQ

- **优点**：
  - 专业消息队列，功能最完整
  - 支持死信队列、优先级、延迟消息
- **缺点**：
  - 新增中间件，运维成本高
  - 对于中规模场景过度设计
  - 与现有微服务技术栈不一致

## 理由

选择 PostgreSQL 轮询是因为：

- 中规模场景（一年数千份文档）下，轮询延迟（5 秒级）完全可接受
- 任务状态与 `document_ingestion_jobs` 表天然合一，无需双写
- 与现有 `ruoyu.student` 的 Worker 模式一致，团队熟悉
- 无需新增中间件，部署简单
- 取消任务只需更新数据库状态，实现最简单

## 后果

- Worker 使用 `BackgroundService`，每 5 秒轮询一次 `status = 'pending'` 的任务
- 需要实现乐观锁：Worker 取任务时将 `status` 从 `pending` 更新为 `processing`，使用 `UPDATE ... WHERE status = 'pending'` 保证只有一个 Worker 取到
- 未来如需更高吞吐或更低延迟，可切换为 Redis Stream，接口层无需变更
