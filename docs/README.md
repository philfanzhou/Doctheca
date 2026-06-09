# ruoyu.docretrieval

> 文档检索域微服务：自带 Web 管理界面用于文档导入与管理，对外通过 gRPC 暴露查询定位能力。

## 状态

- 当前阶段：**阶段 A 待启动**
- 当前文档形态：**按功能模块组织**
- 当前目标：先完成文档结构梳理，再进入实现阶段

## 目录结构

```text
backend/ruoyu.docretrieval/docs/
├── README.md
├── overview/
├── modules/
│   ├── ingestion/
│   ├── management/
│   └── retrieval/
├── architecture/
│   └── adr/
└── delivery/
```

## 导航

| 目录 | 用途 | 入口 |
|------|------|------|
| `overview/` | 服务总述、边界、术语 | [overview/README.md](./overview/README.md) |
| `modules/ingestion/` | 文档导入模块 | [modules/ingestion/README.md](./modules/ingestion/README.md) |
| `modules/management/` | 文档管理模块 | [modules/management/README.md](./modules/management/README.md) |
| `modules/retrieval/` | 查询定位模块 | [modules/retrieval/README.md](./modules/retrieval/README.md) |
| `architecture/` | 跨模块技术主干与 ADR | [architecture/README.md](./architecture/README.md) |
| `delivery/` | 验收、任务、维护流程 | [delivery/README.md](./delivery/README.md) |

## 按角色阅读

- 业务读者：先看 [overview/service-scope.md](./overview/service-scope.md)
- 研发读者：先看 [architecture/system-architecture.md](./architecture/system-architecture.md)
- 模块负责人：直接进入对应 `modules/<module>/`
- 执行者：看 [delivery/tasks.md](./delivery/tasks.md) 与 [delivery/checklist.md](./delivery/checklist.md)

## 关键决策

- 全 .NET： [ADR 0002](./architecture/adr/0002-language-stack.md)
- Qdrant： [ADR 0003](./architecture/adr/0003-vector-database.md)
- SeaweedFS： [ADR 0004](./architecture/adr/0004-document-storage.md)
- PostgreSQL 轮询队列： [ADR 0005](./architecture/adr/0005-ingestion-queue.md)

## 维护规则

- 业务边界与术语：改 `overview/`
- 模块能力：改对应 `modules/`
- 跨模块架构：改 `architecture/`
- 验收与任务：改 `delivery/`
- 关键新决策：新增 ADR
