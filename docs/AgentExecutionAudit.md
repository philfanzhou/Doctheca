# Agent Execution Audit

## 1. 仍需人工介入的功能点

### 集成测试 — 完整解析流程 (TASK-016)

- **当前阻塞原因**：需要运行中的 OpenSearch 服务和数据库（PostgreSQL/SQLite）才能执行端到端测试
- **已做过哪些深度尝试**：
  - 已为 IngestionWorker 编写 6 个单元测试（mock 所有外部依赖）
  - 已为 OpenSearchIndexService 编写 7 个 JSON 响应解析测试（提取解析逻辑为可测试方法）
  - 已为 DocumentDomainService 补齐状态管理测试
  - 已为 DocumentParserService 补齐解析逻辑测试
- **仍缺少什么条件**：运行中的 OpenSearch 实例和数据库实例
- **建议人工如何继续**：
  1. 启动 OpenSearch Docker 容器（`scripts/1.env/start-opensearch.sh`）
  2. 配置 `appsettings.json` 中的连接字符串
  3. 执行端到端测试：上传文档 → Worker 处理 → 数据库验证 → OpenSearch 索引验证

### OpenSearchIndexService 完整单元测试

- **当前阻塞原因**：`OpenSearchIndexService` 直接依赖 `OpenSearchLowLevelClient` 具体类，无法在 UT 中 mock
- **已做过哪些深度尝试**：
  - 提取 JSON 响应解析逻辑为独立测试方法，覆盖 7 个场景
  - 评估了引入 `IOpenSearchLowLevelClient` 接口的方案，但改动范围过大
- **仍缺少什么条件**：需要重构 `OpenSearchIndexService` 以接受可 mock 的客户端抽象
- **建议人工如何继续**：
  1. 引入 `IOpenSearchClient` 接口包装 `OpenSearchLowLevelClient`
  2. 或使用 OpenSearch 提供的测试专用 `InMemoryConnection` 进行集成式单元测试

## 2. 风险与人工后续动作

1. **部署环境验证**：本轮代码改动（常量统一、接口提取、域值英文化）需在服务器上重新构建并验证
2. **数据库域值兼容性**：`SubjectEnglish` 从 `"英语"` 改为 `"English"`，如果数据库中已有使用 `"英语"` 的记录，需要数据迁移
3. **start-qdrant.sh 脚本**：已标注 DEPRECATED，人工可决定是否删除
