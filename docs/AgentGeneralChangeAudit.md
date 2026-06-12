# Agent General Change Audit

## 1. 本轮任务范围

确认并更新 OpenSearch 和 Qdrant Docker 镜像版本及 NuGet 客户端包版本，更新根目录 `scripts/1.env/` 下的启动脚本中的镜像版本配置，更新 csproj 中的 NuGet 包版本，并同步更新 ruoyu.docretrieval 项目正式文档中的版本说明。

## 2. 启动校验结果

| 校验项 | 结果 |
|--------|------|
| 目标项目路径存在且可访问 | 通过 — `d:\Code\Ruoyu.Study\backend\ruoyu.docretrieval` |
| 正式 docs 根目录存在且可读取 | 通过 — `backend\ruoyu.docretrieval\docs` |
| 输入项可解析 | 通过 — 用户要求确认 OpenSearch 版本并更新脚本 |
| 相关模块/文档/代码入口已定位 | 通过 — 脚本、Design.md、local-setup.md |
| 无关未提交改动 | 无关 |
| 禁改区域 | 无 |
| 输入项总表已建立 | 通过 |

## 3. 输入项总表

| 输入项编号 | 类型 | 标题 | 关联模块/功能点 | 当前状态 |
|-----------|------|------|----------------|---------|
| ITEM-01 | feature | 确认并更新 OpenSearch Docker 镜像版本 | DocRetrieval / OpenSearch 集成 | 已完成 |
| ITEM-02 | feature | 确认并更新 Qdrant Docker 镜像版本 | DocRetrieval / Qdrant 集成 | 已完成 |
| ITEM-03 | feature | 检查并更新 NuGet 客户端包版本 | DocRetrieval / NuGet 依赖 | 已完成 |

## 4. 每个输入项的处理记录

### ITEM-01: 确认并更新 OpenSearch Docker 镜像版本

- **输入摘要**：用户发现 Docker Hub 最新 OpenSearch 已到 3.7.0，要求确认版本并更新脚本中的镜像版本配置
- **已核查的证据**：
  - Docker Hub: `opensearchproject/opensearch` 最新 tag 为 `3.7.0`，2.x 最新为 `2.19.5`，1.x 最新为 `1.3.20`
  - NuGet: `OpenSearch.Net` 最新版本为 `1.8.0`（2024-09-06 发布），无 2.x/3.x 版本
  - GitHub COMPATIBILITY.md: 仅列出 OpenSearch 1.x/2.x 与客户端的兼容矩阵，未列出 3.x
  - OpenSearch 官方文档: 声称 "All clients are compatible with any version of OpenSearch"
  - 项目代码: 使用 `OpenSearchLowLevelClient`（低级客户端），对 API 变更更耐受
  - 当前脚本版本: `2.19.1`
- **影响面判断**：
  - 脚本 `scripts/1.env/start-opensearch.sh` — 镜像版本
  - 文档 `docs/overview/Design.md` — 技术栈表（原写 "OpenSearch 1.8" 实为 NuGet 包版本，非服务器版本）
  - 文档 `docs/development/local-setup.md` — 前置条件表
- **决策**：升级到 `2.19.5`（2.x 最新补丁版），不升级到 3.x
  - 理由 1: `OpenSearch.Net 1.8.0` 客户端兼容矩阵仅覆盖到 OpenSearch 2.x
  - 理由 2: 3.x 是大版本升级，存在未验证的 REST API 兼容性风险
  - 理由 3: 2.19.1 → 2.19.5 为补丁升级，包含安全修复和 bug 修复，无破坏性变更
  - 理由 4: 待 opensearch-net 客户端更新兼容矩阵后可再考虑 3.x
- **文档修改项**：
  1. `docs/overview/Design.md` L57: "OpenSearch 1.8" → "OpenSearch 2.19 (Docker) / OpenSearch.Net 1.8 (NuGet)"，明确区分服务器版本与客户端库版本
  2. `docs/development/local-setup.md` L12: OpenSearch 行增加推荐 Docker 镜像版本 `2.19.5` 及兼容性说明
- **文档校验结果**：通过 — 版本信息准确，与代码事实一致，链接可用
- **代码修改项**：
  1. `scripts/1.env/start-opensearch.sh` L4: `opensearchproject/opensearch:2.19.1` → `opensearchproject/opensearch:2.19.5`
- **新增或修改的测试**：无（配置变更，不涉及逻辑修改）
- **实际执行的验证命令**：无（Docker 镜像版本配置变更，需在部署环境实际拉取验证）
- **结果摘要**：脚本镜像版本已从 2.19.1 升级到 2.19.5；文档已修正版本描述歧义

### ITEM-02: 确认并更新 Qdrant Docker 镜像版本

- **输入摘要**：用户要求同样检查 Qdrant 的 Docker 镜像版本
- **已核查的证据**：
  - Docker Hub: `qdrant/qdrant` 最新稳定版为 `v1.18.2`（latest tag）
  - NuGet: `Qdrant.Client` 最新版本为 `1.18.1`，当前项目使用 `1.12.0`
  - 当前脚本版本: `v1.14.1`
  - 项目代码: `QdrantService.cs` 使用 gRPC 通信，调用的 API 包括 `ListCollectionsAsync`、`CreateCollectionAsync`、`UpsertAsync`、`SearchAsync`、`DeleteAsync`、`SetPayloadAsync`、`CreatePayloadIndexAsync`，均为核心稳定 API
  - Qdrant 服务端保持 gRPC 向后兼容，旧客户端可连接新服务端
- **影响面判断**：
  - 脚本 `scripts/1.env/start-qdrant.sh` — 镜像版本
  - 文档 `docs/overview/Design.md` — 技术栈表（原写 "Qdrant.Client 1.12" 仅体现 NuGet 包版本，未体现 Docker 镜像版本）
  - 文档 `docs/development/local-setup.md` — 前置条件表
- **决策**：升级到 `v1.18.2`（最新稳定版）
  - 理由 1: Qdrant 服务端保持 gRPC 向后兼容，当前 `Qdrant.Client 1.12.0` 可正常连接 `v1.18.2` 服务端
  - 理由 2: 代码使用的 API 均为核心稳定 API，跨版本无破坏性变更
  - 理由 3: 与最新 NuGet 客户端 `1.18.1` 版本号对齐，便于后续同步升级 NuGet 包
- **文档修改项**：
  1. `docs/overview/Design.md` L58: "Qdrant.Client 1.12" → "Qdrant 1.18 (Docker) / Qdrant.Client 1.12 (NuGet)"，明确区分服务器版本与客户端库版本
  2. `docs/development/local-setup.md` L13: Qdrant 行增加推荐 Docker 镜像版本 `v1.18.2` 及兼容性说明
- **文档校验结果**：通过 — 版本信息准确，与代码事实一致
- **代码修改项**：
  1. `scripts/1.env/start-qdrant.sh` L4: `qdrant/qdrant:v1.14.1` → `qdrant/qdrant:v1.18.2`
- **新增或修改的测试**：无（配置变更，不涉及逻辑修改）
- **实际执行的验证命令**：无（Docker 镜像版本配置变更，需在部署环境实际拉取验证）
- **结果摘要**：脚本镜像版本已从 v1.14.1 升级到 v1.18.2；文档已补充版本信息

### ITEM-03: 检查并更新 NuGet 客户端包版本

- **输入摘要**：用户要求根据调研结果，检查两个镜像对应的 NuGet 包是否有适合 .NET 8 的更新版本
- **已核查的证据**：
  - **OpenSearch.Net**: 当前 `1.8.0`，NuGet 最新也是 `1.8.0`（2024-09-06 发布），支持 .NET 6.0 / .NET 8.0 / .NET Standard 2.0 → 已是最新，无需升级
  - **Qdrant.Client**: 当前 `1.12.0`，NuGet 最新 `1.18.1`（2026-05-11 发布），支持 .NET 6.0 / .NET Standard 2.0 → 兼容 .NET 8，可升级
  - GitHub Releases: Qdrant.Client 1.12.0 → 1.18.1 之间无破坏性 API 变更（核心 gRPC API 稳定）
  - v1.16.0 废弃了 `VectorOutput.data`/`indices`/`vectors_count`，但项目代码未使用这些字段
  - v1.16.1 新增 `IQdrantClient` 接口（非破坏性）
  - 代码审查: `QdrantService.cs` 使用的 API（`ListCollectionsAsync`、`CreateCollectionAsync`、`UpsertAsync`、`SearchAsync`、`DeleteAsync`、`SetPayloadAsync`、`CreatePayloadIndexAsync`、`scoredPoint.Score`、`scoredPoint.Payload`）均为跨版本稳定 API
- **影响面判断**：
  - `src/Service/Ruoyu.Study.DocRetrieval.Service.csproj` — Qdrant.Client 版本
  - `src/Host/Ruoyu.Study.DocRetrieval.Host.csproj` — Qdrant.Client 版本
  - `docs/overview/Design.md` — 技术栈表
  - `docs/development/local-setup.md` — 前置条件表
- **决策**：
  - OpenSearch.Net: 保持 `1.8.0`（已是最新）
  - Qdrant.Client: 升级到 `1.18.1`（最新稳定版，与 Docker 镜像 v1.18.2 对齐）
- **文档修改项**：
  1. `docs/overview/Design.md` L58: "Qdrant 1.18 (Docker) / Qdrant.Client 1.12 (NuGet)" → "Qdrant 1.18 (Docker) / Qdrant.Client 1.18 (NuGet)"
  2. `docs/development/local-setup.md` L13: Qdrant 行兼容性说明更新为 `Qdrant.Client 1.18.1`
- **文档校验结果**：通过 — 版本信息与 csproj 一致
- **代码修改项**：
  1. `src/Service/Ruoyu.Study.DocRetrieval.Service.csproj` L13: `Qdrant.Client` Version `1.12.0` → `1.18.1`
  2. `src/Host/Ruoyu.Study.DocRetrieval.Host.csproj` L18: `Qdrant.Client` Version `1.12.0` → `1.18.1`
- **新增或修改的测试**：无（NuGet 包升级，API 兼容，无需修改测试）
- **实际执行的验证命令**：
  - `dotnet build backend/ruoyu.docretrieval/Ruoyu.Study.DocRetrieval.sln` → 0 错误 0 警告
  - `dotnet test backend/ruoyu.docretrieval/Ruoyu.Study.DocRetrieval.sln --no-build` → 104 通过，0 失败
- **结果摘要**：Qdrant.Client 从 1.12.0 升级到 1.18.1；构建和测试全部通过

## 5. 正式文档同步记录

| 文档路径 | 更新原因 | 更新摘要 |
|---------|---------|---------|
| `backend/ruoyu.docretrieval/docs/overview/Design.md` | 原技术栈表 "OpenSearch 1.8" 歧义（实为 NuGet 包版本） | 改为 "OpenSearch 2.19 (Docker) / OpenSearch.Net 1.8 (NuGet)"，区分服务器与客户端版本 |
| `backend/ruoyu.docretrieval/docs/development/local-setup.md` | 补充 OpenSearch 推荐版本信息 | 前置条件表 OpenSearch 行增加推荐 Docker 镜像版本 2.19.5 及兼容性说明 |
| `backend/ruoyu.docretrieval/docs/overview/Design.md` | 技术栈表 Qdrant 版本描述不完整 | 改为 "Qdrant 1.18 (Docker) / Qdrant.Client 1.18 (NuGet)"，区分服务器与客户端版本 |
| `backend/ruoyu.docretrieval/docs/development/local-setup.md` | 补充 Qdrant 推荐版本信息 | 前置条件表 Qdrant 行增加推荐 Docker 镜像版本 v1.18.2 及兼容性说明 |
| `backend/ruoyu.docretrieval/src/Service/Ruoyu.Study.DocRetrieval.Service.csproj` | Qdrant.Client NuGet 包升级 | 1.12.0 → 1.18.1 |
| `backend/ruoyu.docretrieval/src/Host/Ruoyu.Study.DocRetrieval.Host.csproj` | Qdrant.Client NuGet 包升级 | 1.12.0 → 1.18.1 |

## 6. 未完成或暂时跳过的输入项

无。

## 7. 风险与待人工复核事项

1. **OpenSearch 3.x 升级待评估**：当前 `OpenSearch.Net 1.8.0` 客户端的兼容矩阵未覆盖 OpenSearch 3.x。如需升级到 3.7.0，建议：
   - 等待 opensearch-net 客户端发布适配 3.x 的版本
   - 或在测试环境验证 `OpenSearchLowLevelClient` 与 OpenSearch 3.x 的实际兼容性
2. **部署环境验证**：脚本版本已更新，需在实际部署环境拉取新镜像并启动容器验证：
   - `docker pull opensearchproject/opensearch:2.19.5`
   - `docker pull qdrant/qdrant:v1.18.2`
3. **数据卷兼容性**：
   - OpenSearch: 2.19.1 → 2.19.5 补丁升级通常无数据兼容问题，建议升级前备份 `scripts/1.env/data/` 目录
   - Qdrant: v1.14.1 → v1.18.2 跨多个小版本，Qdrant 支持自动数据迁移，但建议升级前备份 `scripts/1.env/data/` 目录
