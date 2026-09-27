# Doctheca 协作规范

Doctheca 是通用的文档库与检索服务：.NET 最小 API、Vue 3 管理前端、单容器部署（API + SPA 同进程，容器内监听 5012）。文档解析与 Office→PDF 转换委托外部 [StructaDoc](https://github.com/philfanzhou/StructaDoc) 服务（ADR-0009），本服务只保存引用与本地 Blocks/Images 同步副本，并提供全文精确检索。它于 2026-09-26 从 Ruoyu.Study monorepo 的 `ruoyu.doclibrary`（更早名 `ruoyu.docretrieval`）迁出，保留子树提交历史，采用 MIT License。

## 维护方式

- 本文件是 AI 协作流程、项目边界和验证约束的统一入口。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- `main` 受 ruleset 保护：禁止直接推送、强推和删除，只能通过 PR 合并，且必须通过 `Build & Test`、`Analyze (csharp)`、`Analyze (javascript-typescript)` 检查。
- CI 见 `.github/workflows/ci.yml` 与 `codeql.yml`；推送前仍须本地运行「验证」一节的全部命令。
- 版本发布只通过推送 `MAJOR.MINOR.PATCH`（或 `-rc.NUMBER`）tag 触发，流程见 `docs/development/Deployment.md`「版本发布」一节；不得手工创建 Release 或推送镜像。

## 文档与沟通语言

- 流程与约束文档（本文件）、GitHub issue/PR 正文和 review 全程使用中文；Issue 标题使用中文，建议格式为 `[模块] 简明动作`。
- PR 标题和 commit message 使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` / `ci:` 等）；subject 说明做了什么，需要时用 body 说明原因。
- `README.md`、`CONTRIBUTING.md`、`SECURITY.md`、`docs/`、`CONTEXT.md`、代码注释、日志与 API/异常消息等面向使用者的文字使用英文。
- 从 Ruoyu.Study 继承的中文文档、注释与日志的英文化由 #2 跟踪：新增内容直接用英文；修改现有中文内容时不顺带整篇翻译，翻译不与功能改动混在同一提交。
- 业务数据值（如 `src/Common/Constants/*` 中的学科、年级取值）、LLM 提示词与管理端 UI 文案不属于上述翻译范围。
- 代码标识符、配置键、JWT claim、HTTP 路由、命令和路径保持原样。

## 贡献约定

- `CONTRIBUTING.md` 是所有贡献者的工程入口；漏洞通过 `SECURITY.md` 所述的私密渠道报告，不开公开 issue。
- Issue 使用 `.github/ISSUE_TEMPLATE/task.md`，PR 使用 `.github/pull_request_template.md`，按模板逐节填写，不适用的节写明“无”或“不适用”及理由。

## 项目边界与架构

- 领域语言见根目录 `CONTEXT.md`（Document Library：Document File / Document Parse / Parse Block / Parse Image）。
- `src/Common`（Doctheca.Common）、`src/Ai`（Doctheca.Ai）、`src/Consul`（Doctheca.Consul）是从 Ruoyu.Study 的 `ruoyu.common` **复制**而来的类库（2026-09-26 快照），没有编译期上游同步；上游修复需人工评估是否回合。不得反向引用 `src/Host`。其中 `Doctheca.Ai` 依赖 `Doctheca.Common`。
- 依赖方向：Host → Service → Domain → Database；Host/Domain → Common/Ai/Consul。
- 分层：`src/Database`（EF Core 实体与仓储）、`src/Domain`（领域服务与模型）、`src/Service`（最小 API 端点、StructaDoc 客户端、OpenSearch、解析同步、LLM 分析）、`src/Host`（宿主组合、admin 认证、Consul、Serilog/Loki、wwwroot SPA）、`src/Tests`。
- **解析委托边界（ADR-0009）**：新文档原件与解析产物由 StructaDoc 主责存储，Doctheca 只保存 documentId/parseRunId 引用和本地 Blocks/Images 副本，一律经 StructaDoc 版本化 API + scoped ApiKey，不直连其数据库或对象存储。
- 认证：管理端使用 SignaCore（或兼容 OIDC discovery/JWKS 的签发方）的 `role=admin`，HttpOnly Cookie/JWT 会话；`/admin/auth/login|refresh|logout` 匿名，其余 `/admin/*` 要求 `DocthecaAdmin` 策略。
- 检索：OpenSearch 全文精确搜索 + block 索引；OpenSearch 写失败不阻塞解析主流程，查询失败返回空结果并记录 Warning。
- 存量兼容：SeaweedFS/MinIO/LocalFile 仅用于迁移前上传对象与解析图片的只读兼容与删除清理，新数据不再写入。
- 数据库为 PostgreSQL（默认库名 `doctheca`）。API 监听端口 5012（`Program.cs` 的 `Endpoints__Http`）；改端口属部署契约变更，须同步 `start.sh`、`Dockerfile` 与部署文档。

## 验证

在仓库根目录依次运行：

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build
cd frontend && npm ci && npm run build
```

## 安全

- 删除、移动或批量改写前先验证精确目标。
- 配置中的凭据（数据库、Consul token、S3 密钥、StructaDoc ApiKey、Identity AppSecret、LLM ApiKey）一律经环境变量或 Consul KV 注入，不得写入仓库。
- 绝不提交或记录连接字符串、S3 密钥、Consul token、StructaDoc ApiKey、Identity AppSecret、LLM ApiKey、JWT、authorization header 或个人数据；日志和测试输出必须脱敏。
- 增加依赖、改变公开 API、数据库、配置或部署方式前，先说明兼容性、迁移和回滚影响。
- 提交前检查文档链接、模板格式、secret 和仓库状态。
