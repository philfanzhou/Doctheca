# 总览文档 (Overview)

> 本目录收拢了 `ruoyu.docretrieval` 服务的所有服务级总览文档。根目录 `docs/` 不再散落正式总览正文。

## 文档索引

| 文档 | 用途 |
|------|------|
| [SystemContext.md](SystemContext.md) | 服务定位、上下游、参与者 |
| [Integration.md](Integration.md) | 集成矩阵、接口边界、失败语义 |
| [KeyFlows.md](KeyFlows.md) | 关键跨服务时序与调用链（3 张 ASCII 时序图） |
| [DataOwnership.md](DataOwnership.md) | 数据主责、引用边界、双写禁区 |
| [Requirements.md](Requirements.md) | 服务级需求摘要 → 模块详细需求入口 |
| [Design.md](Design.md) | 服务级架构：分层、技术栈、关键决策 |

## 阅读建议

- **第一次了解项目**：[SystemContext.md](SystemContext.md) → [Design.md](Design.md) → [KeyFlows.md](KeyFlows.md)
- **查接口和集成**：[Integration.md](Integration.md)
- **查需求**：[Requirements.md](Requirements.md) → 跳转对应模块
- **查数据归属**：[DataOwnership.md](DataOwnership.md)
