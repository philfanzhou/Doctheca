# 服务边界

## 服务定位

`ruoyu.docretrieval` 是独立的文档检索域微服务，负责把英文教材、真题、讲义等文档转化为可查询的检索底座。

服务提供两类入口：

- Web 管理界面：用于导入与管理文档
- gRPC 查询接口：用于给上游业务系统提供查询定位能力

## 对外能力

### 管理侧能力

- 上传文档
- 查看导入状态
- 查看文档列表
- 修改文档元数据
- 删除文档

### 业务侧能力

- 按单词查询
- 按短语查询
- 返回文档名、页码、句子或题目文本
- 支持过滤、分页、排序和锚点定位

## 服务边界

### 本服务负责

- 文档导入受理
- 异步解析、OCR、建索引
- 精确检索
- 语义召回
- 管理文档元数据与生命周期

### 本服务不负责

- 聊天式问答
- 释义、翻译、改写
- 错题、作业、题库业务本身
- 终端用户账号、权限、配额
- 上游业务展示逻辑

## 调用边界

- 管理员 / 运营人员通过本服务自带的 Web 管理界面操作文档
- 业务系统只能调用 gRPC 查询接口
- 终端用户不直接访问本服务

## 文档阅读边界

- 看导入：去 [modules/ingestion/README.md](../modules/ingestion/README.md)
- 看管理：去 [modules/management/README.md](../modules/management/README.md)
- 看查询：去 [modules/retrieval/README.md](../modules/retrieval/README.md)
- 看技术主干：去 [architecture/README.md](../architecture/README.md)
