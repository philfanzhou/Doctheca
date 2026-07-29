# DataOwnership — 数据主责与引用边界

## 本服务拥有的数据实体（主责）

以下数据由 DocLibrary 服务独立管理，其他服务不可直接写入：

| 实体表 | 说明 | 操作权限 |
|--------|------|---------|
| `document_files` | 文档文件表 | 本服务独写 |
| `document_parses` | 解析记录表 | 本服务独写 |
| `document_parse_blocks` | 解析 block 表 | 本服务独写 |
| `document_parse_images` | 解析图片表 | 本服务独写 |

## 本服务引用的外部数据（只读引用）

| 外部数据 | 来源 | 读取方式 | 说明 |
|---------|------|---------|------|
| Identity JWT | QuantumZhou.Identity | OIDC/JWKS + HTTP token API | 只消费管理员身份，不持久化 Identity 账户 |

## 外部系统依赖

| 系统 | 写入 | 读取 | 说明 |
|------|------|------|------|
| PostgreSQL | CRUD 所有表 | 全文搜索、文档查询 | 自建数据库 |
| MinIO / SeaweedFS | 上传文件 | 下载文件 | OSS 对象存储 |
| OpenSearch | 创建/删除索引 | 搜索查询 | 外部检索引擎 |

## 双写禁区

- 本服务不写入任何其他 Ruoyu 微服务的数据库
- 其他服务不应直接写入本服务的 4 张业务表
- QuestionBank 的导入状态和题目 ID 由 QuestionBank 自己持久化，DocLibrary 不保存副本
- OSS 文件路径格式由本服务内部控制，其他服务不应直接操作
