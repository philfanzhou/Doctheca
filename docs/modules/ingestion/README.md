# 文档导入模块

本模块负责把一份原始文档变成“可被查询”的检索底座。

## 覆盖范围

- 上传文档
- 同步校验
- 导入任务受理
- 异步解析 / OCR / 建索引
- 导入状态查看

## 文档列表

| 文档 | 用途 |
|------|------|
| [requirements.md](./requirements.md) | 导入能力的业务范围与约束 |
| [process.md](./process.md) | 导入流、状态机、异常分支 |
| [interfaces.md](./interfaces.md) | 上传接口、状态查看接口 |
| [technical-design.md](./technical-design.md) | 解析器、OCR、队列、任务状态模型 |

## 上下游关系

- 上游入口：Web 管理界面
- 下游支撑：解析器、OCR、OpenSearch、Qdrant、PostgreSQL、SeaweedFS
- 成功产物：可进入查询定位模块的已就绪文档
