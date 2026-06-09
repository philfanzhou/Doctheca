# 文档维护流程

## 修改规则

- 改服务定位、边界、术语：改 `overview/`
- 改导入、管理、查询模块内容：改 `modules/` 下对应模块
- 改分层架构、数据模型、存储、中间件：改 `architecture/`
- 改验收与实施计划：改 `delivery/`

## 新增决策规则

- 新的关键技术决策必须新增 ADR
- 不直接覆盖旧 ADR 的结论

## 旧入口策略

顶层旧文件 `requirements.md`、`process.md`、`spec.md` 不再作为主文档维护，只保留迁移说明与新入口导航。

## 评审建议

- 业务评审：优先看模块下的 `requirements.md`
- 流程评审：优先看模块下的 `process.md`
- 技术评审：优先看 `architecture/` 与模块下的技术文档
- 交付评审：优先看 `delivery/`
