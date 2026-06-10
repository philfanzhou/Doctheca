# DocRetrieval 文档

> 本目录是 `ruoyu.docretrieval` 服务的文档入口。正式总览文档已收拢到 `overview/` 目录。

## 导航

| 入口 | 说明 |
|------|------|
| [overview/](overview/README.md) | 服务级总览文档（系统上下文、集成、流程、需求、设计、规范） |
| [modules/](modules/README.md) | 内部业务能力（7 个功能域 × 6 件套） |
| [database/](database/README.md) | 数据库唯一事实源（6 张表结构） |
| [Integration/](Integration/README.md) | 外部系统交互说明 |
| [development/](development/README.md) | 本地搭建、运行调试、验证方法 |
| [AgentReviewNotes.md](AgentReviewNotes.md) | 审阅记录与待人工审核事项 |

## 阅读建议

- **第一次了解项目**：先看 [overview/](overview/README.md)
- **查某个 API**：看 [overview/Integration.md](overview/Integration.md) → 跳转 [modules/](modules/README.md)
- **查表结构**：直接看 [database/](database/README.md)
- **本地跑起来**：看 [development/](development/README.md)
