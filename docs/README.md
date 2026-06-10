# DocRetrieval 服务文档

## 文档地图

### 顶层总览

| 文档 | 用途 |
|------|------|
| [SystemContext.md](SystemContext.md) | 服务定位、上下游、参与者 |
| [Integration.md](Integration.md) | 集成矩阵、接口边界、失败语义 |
| [KeyFlows.md](KeyFlows.md) | 关键跨服务时序与调用链（3 张 ASCII 时序图） |
| [DataOwnership.md](DataOwnership.md) | 数据主责、引用边界、双写禁区 |
| [Requirements.md](Requirements.md) | 服务级需求摘要 → 模块详细需求入口 |
| [Design.md](Design.md) | 服务级架构：分层、技术栈、关键决策 |
| [DotNetCodingPolicy.md](DotNetCodingPolicy.md) | 编码规范（解决方案级共享 + 本服务补充） |
| [AgentReviewNotes.md](AgentReviewNotes.md) | 本轮文档审阅记录与待人工审核事项 |

### 统一事实源

| 目录 | 用途 |
|------|------|
| [database/](database/README.md) | 数据库唯一事实源：6 张表、实体关系、迁移历史 |

### 内部业务能力

| 目录 | 用途 |
|------|------|
| [modules/](modules/README.md) | 7 个业务功能的完整 6 件套文档 |

### 外部系统交互

| 目录 | 用途 |
|------|------|
| [Integration/](Integration/README.md) | 外部基础设施交互说明 |

### 开发执行支持

| 目录 | 用途 |
|------|------|
| [development/](development/README.md) | 本地搭建、运行调试、验证方法 |

## 阅读建议

- **第一次了解项目**：先看 [SystemContext.md](SystemContext.md) → [Design.md](Design.md) → [KeyFlows.md](KeyFlows.md)
- **查某个 API**：看 [Integration.md](Integration.md) 找到接口，再跳转到 [modules/](modules/README.md) 对应功能
- **查表结构**：直接看 [database/](database/README.md) → tables/
- **查需求**：看 [Requirements.md](Requirements.md) → 跳转对应模块
- **本地跑起来**：看 [development/](development/README.md)