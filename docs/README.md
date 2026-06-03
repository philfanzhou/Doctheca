# ruoyu.docretrieval

> 文档检索域微服务：自带 Web 管理界面用于文档上传 / 删除，对外通过 gRPC 暴露查询定位能力（按单词 / 短语 → 返回真实出现位置与上下文）。

## 状态

- 当前阶段：**阶段 A 待启动**
- 文档模式：**Spec 模式**（仅完成文档梳理，未开始编码）

## 目录结构

```
backend/ruoyu.docretrieval/
└── docs/                       # 文档
    ├── README.md               # 本文件（入口）
    ├── requirements.md         # 业务需求（仅业务视角）
    ├── process.md              # 业务流程（业务视角：参与者、流程、状态机、异常分支）
    ├── spec.md                 # 主规格说明（仅技术视角）
    ├── checklist.md            # 验收清单
    └── tasks.md                # 实施任务列表
```

> 代码与配置目录（`src/Contract`、`src/Database`、`src/Domain`、`src/Host`、`src/Service`、`test/` 等）将在阶段 A 评审通过后按 [tasks.md](./tasks.md) 创建。

## 文档导航

| 文档 | 视角 | 用途 |
|------|------|------|
| [requirements.md](./requirements.md) | 业务 | 业务背景、能力范围、接口说明（上传/删除通过 Web 管理界面，查询通过 gRPC）、验收口径、业务不变式 |
| [process.md](./process.md) | 业务 | 业务流程：角色边界、三条业务流（导入/查询/删除）、状态机、异常分支 |
| [spec.md](./spec.md) | 技术 | 服务规格说明（分层架构、数据模型、gRPC 查询接口、Web 管理接口、技术选型、验收指标） |
| [checklist.md](./checklist.md) | 验收 | 阶段 A/B/C 验收项清单与签字栏 |
| [tasks.md](./tasks.md) | 实施 | 阶段 A/B/C 实施任务拆解与状态总览 |

> 业务范围变更请改 [requirements.md](./requirements.md)，业务流程变更请改 [process.md](./process.md)；技术实现变更请改 [spec.md](./spec.md)；不要在 spec 中混入业务或流程描述。

## 关键设计决策（一句话版）

- **架构**：文档解析（Python）+ 精确检索（OpenSearch）+ 语义召回（Qdrant/pgvector）+ 编排返回（.NET gRPC）
- **核心抽象**："文档展示锚点"是一等数据，所有检索结果必须可还原到文档 / 页码 / 块 / 句 / 题号
- **明确不做**：聊天式伪 RAG、纯向量库作主检索、把检索能力塞进现有背单词服务

## 关联文档

- [原始讨论计划](../../../plan/english-doc-rag-scheme-plan.md) — 选型背景
- [系统架构文档](../../architecture.md) — 微服务体系全局视角
- [部署规格文档](../../deployment.md) — 部署模式参考
- [数据库规格文档](../../database-spec.md) — 数据库命名与迁移规范
- [统一错误处理规范](../../error-handling.md) — gRPC 错误规范
- [提交前检查规格](../../../.testcode/docs/pre-commit-check.md) — 代码提交前 4 阶段检查

## 待敲定事项（阶段 A 启动前必须完成）

- [ ] 语言栈：混合（.NET + Python） / 全 Python / 全 .NET
- [ ] 向量库：Qdrant / pgvector
- [ ] 文档存储：复用 SeaweedFS / 单独对象存储

详见 [spec.md §9.3](./spec.md#93-待决策需在阶段-a-启动前敲定)。
