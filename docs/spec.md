# 迁移说明：旧技术规格入口

本文件不再承载 `ruoyu.docretrieval` 的完整技术规格正文，规格文档已按模块与架构层拆分。

## 新入口

- 服务总览： [overview/README.md](./overview/README.md)
- 导入技术设计： [modules/ingestion/technical-design.md](./modules/ingestion/technical-design.md)
- 管理技术设计： [modules/management/technical-design.md](./modules/management/technical-design.md)
- 查询协议与检索设计： [modules/retrieval/README.md](./modules/retrieval/README.md)
- 架构总览： [architecture/README.md](./architecture/README.md)
- 交付治理： [delivery/README.md](./delivery/README.md)

## 说明

- 旧结构将总体架构、数据模型、接口设计、技术选型全部混在 `spec.md`
- 新结构把模块内规格收敛到 `modules/`，把跨模块主干收敛到 `architecture/`
- 后续技术规格修改请直接进入对应模块目录或 `architecture/`，不再回写本文件
