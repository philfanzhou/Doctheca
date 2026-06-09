# ruoyu.docretrieval

> 文档检索域微服务：自带 Web 管理界面用于文档导入与管理，对外通过 gRPC 暴露查询定位能力。

## 目录结构

```text
backend/ruoyu.docretrieval/docs/
├── README.md                     ← 本文件
└── modules/                      ← 功能模块文档（每个 feature 一个文件夹）
    ├── DocumentUpload/           ← 文档上传与导入任务创建
    ├── DocumentParsing/          ← 文档解析与结构化处理
    ├── DocumentList/             ← 文档列表查询与筛选
    ├── DocumentMetadata/         ← 文档元数据更新
    ├── DocumentDeletion/         ← 文档删除与级联清理
    ├── ExactSearch/              ← 精确关键词检索
    └── HybridSearch/             ← 混合检索（精确+语义）
```

## 功能模块索引

每个模块包含 6 个标准文档：

| 文件 | 说明 |
|------|------|
| `01-FEATURE.md` | 功能概述、用户故事、验收条件、范围外 |
| `02-SPEC.md` | 功能要求清单、详细验收标准、非功能需求 |
| `03-DESIGN.md` | 目录结构、接口签名、数据流、错误处理 |
| `04-TASKS.md` | 代码审查任务列表与验证命令 |
| `05-TESTS.md` | 单元/集成/边界测试场景与断言 |
| `06-CONVENTIONS.md` | 命名、日志、错误消息、代码风格 |

### 文档导入

| 模块 | 一句话概括 | 入口 |
|------|-----------|------|
| **DocumentUpload** | 管理员上传文档，系统自动创建文档记录和导入任务 | [01-FEATURE.md](./modules/DocumentUpload/01-FEATURE.md) |
| **DocumentParsing** | 后台工作器自动解析文档内容并持久化 | [01-FEATURE.md](./modules/DocumentParsing/01-FEATURE.md) |

### 文档管理

| 模块 | 一句话概括 | 入口 |
|------|-----------|------|
| **DocumentList** | 管理员按条件筛选和查看文档列表 | [01-FEATURE.md](./modules/DocumentList/01-FEATURE.md) |
| **DocumentMetadata** | 管理员修改已就绪文档的元数据 | [01-FEATURE.md](./modules/DocumentMetadata/01-FEATURE.md) |
| **DocumentDeletion** | 管理员删除文档及其所有关联数据 | [01-FEATURE.md](./modules/DocumentDeletion/01-FEATURE.md) |

### 文档检索

| 模块 | 一句话概括 | 入口 |
|------|-----------|------|
| **ExactSearch** | 通过关键词精确检索文档内容 | [01-FEATURE.md](./modules/ExactSearch/01-FEATURE.md) |
| **HybridSearch** | 结合精确匹配和语义相似度检索文档 | [01-FEATURE.md](./modules/HybridSearch/01-FEATURE.md) |

## 按角色阅读

- 业务读者：从各模块的 `01-FEATURE.md` 开始
- 研发读者：从 `03-DESIGN.md` 了解架构和数据流
- 测试人员：从 `05-TESTS.md` 了解测试场景
- 代码审查者：从 `04-TASKS.md` 获取审查任务列表

## 维护规则

- 每个功能模块独立维护，互不依赖
- 接口签名变更需同步更新 `02-SPEC.md` 和 `03-DESIGN.md`
- 新增测试场景需同步更新 `05-TESTS.md`
- 新增 feature 时在此 README 中添加索引
