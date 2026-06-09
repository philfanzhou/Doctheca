# 查询定位模块

本模块负责把“查单词 / 查短语”的请求转成可解释的命中结果。

## 覆盖范围

- 单词查询
- 短语查询
- 精确检索
- 语义召回
- 混合检索
- 排序、去重、分页、高亮、锚点返回

## 文档列表

| 文档 | 用途 |
|------|------|
| [requirements.md](./requirements.md) | 查询能力的业务目标与约束 |
| [process.md](./process.md) | 查询流、去重、排序、无命中处理 |
| [grpc-contract.md](./grpc-contract.md) | gRPC 协议、消息、错误码 |
| [exact-search.md](./exact-search.md) | 精确检索设计 |
| [semantic-retrieval.md](./semantic-retrieval.md) | 语义召回设计 |
| [ranking-pagination.md](./ranking-pagination.md) | 排序、分页、偏移高亮 |
