# Agent General Change Audit

## 1. 本轮任务范围

确认并更新 OpenSearch 和 Qdrant Docker 镜像版本及 NuGet 客户端包版本，更新根目录 `scripts/1.env/` 下的启动脚本中的镜像版本配置，更新 csproj 中的 NuGet 包版本，并同步更新 ruoyu.docretrieval 项目正式文档中的版本说明。后续根据用户需求，移除语义搜索（Qdrant/Embedding/HybridSearch）功能，仅保留精确搜索。

## 2. 启动校验结果

| 校验项               | 结果                                                    |
| ----------------- | ----------------------------------------------------- |
| 目标项目路径存在且可访问      | 通过 — `d:\Code\Ruoyu.Study\backend\ruoyu.docretrieval` |
| 正式 docs 根目录存在且可读取 | 通过 — `backend\ruoyu.docretrieval\docs`                |
| 输入项可解析            | 通过 — 用户要求确认 OpenSearch 版本并更新脚本                        |
| 相关模块/文档/代码入口已定位   | 通过 — 脚本、Design.md、local-setup.md                      |
| 无关未提交改动           | 无关                                                    |
| 禁改区域              | 无                                                     |
| 输入项总表已建立          | 通过                                                    |

## 3. 输入项总表

| 输入项编号   | 类型       | 标题                                      | 关联模块/功能点                     | 当前状态 |
| ------- | -------- | --------------------------------------- | ---------------------------- | ---- |
| ITEM-01 | feature  | 确认并更新 OpenSearch Docker 镜像版本            | DocRetrieval / OpenSearch 集成 | 已完成  |
| ITEM-02 | feature  | 确认并更新 Qdrant Docker 镜像版本                | DocRetrieval / Qdrant 集成     | 已完成  |
| ITEM-03 | feature  | 检查并更新 NuGet 客户端包版本                      | DocRetrieval / NuGet 依赖      | 已完成  |
| ITEM-04 | refactor | 移除语义搜索功能（Qdrant/Embedding/HybridSearch） | DocRetrieval / 搜索架构          | 已完成  |

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

### ITEM-04: 移除语义搜索功能（Qdrant/Embedding/HybridSearch）

- **输入摘要**：用户确认仅需要精确搜索（BM25 关键词匹配），不需要语义搜索和混合搜索。要求移除 Qdrant 向量数据库、SiliconFlow Embedding API、HybridSearch 相关的所有代码、配置、测试和文档，仅保留 OpenSearch 精确搜索。
- **已核查的证据**：
  - 精确搜索（BM25）已能满足需求：搜索关键词 → 匹配文档 → 返回页码+语句/题目
  - 语义搜索需要额外部署 Qdrant + 配置 Embedding API KEY，增加运维复杂度
  - 不配置 API KEY 时，语义搜索和混合搜索功能完全不可用，文档入库也会因向量写入失败而出错
  - 文档解析流程（PDF/Word/PPT → 文本提取 → 段落合并 → 句子切分 → 题目识别 → 词干提取 → 写DB+OpenSearch）不依赖任何模型，仅 Qdrant 向量化步骤需要 Embedding API
  - 评估了 Apache Tika + Elasticsearch 方案（收益不大）和 OpenWebUI RAG 方案（方向不对，是问答系统非搜索引擎），均不建议引入
- **影响面判断**：
  - **代码**：6 个核心代码文件需修改/删除
  - **测试**：4 个测试文件需修改
  - **Proto**：1 个 gRPC 协议文件需修改
  - **配置**：1 个 appsettings.json 需修改
  - **DI 注册**：1 个 Program.cs 需修改
  - **文档**：20+ 文档文件需修改/标注
  - **脚本**：1 个 Docker 启动脚本（start-qdrant.sh）不再需要
- **决策**：完整移除 Qdrant/Embedding/HybridSearch，仅保留 OpenSearch 精确搜索
  - 理由 1: 用户需求明确，仅需精确搜索
  - 理由 2: 移除后降低部署复杂度（无需 Qdrant 容器、无需 Embedding API KEY）
  - 理由 3: 移除后消除文档入库对 Embedding API 的依赖，入库流程更可靠
  - 理由 4: 如未来需要语义搜索，可基于 OpenSearch 自身的 k-NN 插件重新引入，无需 Qdrant
- **代码修改项**：
  1. `src/Service/QdrantService.cs` — 删除
  2. `src/Domain/Repositories/IQdrantService.cs` — 删除
  3. `src/Domain/Models/SearchConfig.cs` — 移除 QdrantOptions 和 EmbeddingOptions 类，保留 OpenSearchOptions
  4. `src/Domain/Services/DocumentDomainService.cs` — 移除 IQdrantService 依赖、构造函数参数、UpdateMetadataAsync 中 Qdrant 向量索引元数据更新代码块、DeleteDocumentAsync 中 Qdrant 向量数据删除代码块
  5. `src/Service/OpenSearchIndexService.cs` — 移除 HybridSearchAsync 方法和 IQdrantService 引用
  6. `src/Domain/Repositories/ISearchIndexService.cs` — 移除 HybridSearchAsync 方法声明
  7. `src/Domain/Services/ISearchDomainService.cs` — 移除 HybridSearchAsync
  8. `src/Domain/Services/SearchDomainService.cs` — 移除 HybridSearchAsync 方法
  9. `src/Service/DocumentRetrievalServiceImpl.cs` — 移除 HybridSearch gRPC 方法重写
  10. `src/Contract/Protos/docretrieval.proto` — 仅保留 ExactSearch RPC，移除 HybridSearch RPC
  11. `src/Host/Program.cs` — 移除 Qdrant/Embedding DI 注册和 Qdrant 初始化块
  12. `src/Host/appsettings.json` — 移除 Qdrant 和 Embedding 配置节
- **测试修改项**：
  1. `test/.../DocumentDomainServiceTests.cs` — 移除 Qdrant mock
  2. `test/.../SearchDomainServiceTests.cs` — 移除 HybridSearch 测试
  3. `test/.../DocumentRetrievalServiceImplTests.cs` — 移除 HybridSearch 测试
  4. `test/.../ConstantsTests.cs` — 移除 QdrantOptions/EmbeddingOptions 测试
- **文档修改项**：
  1. `docs/overview/Design.md` — 移除 Qdrant 技术栈行，移除 QdrantService 引用
  2. `docs/overview/SystemContext.md` — 移除 Qdrant/SiliconFlow 依赖，移除 HybridSearch RPC
  3. `docs/overview/Integration.md` — 移除 Qdrant/SiliconFlow 集成行和 HybridSearch RPC
  4. `docs/development/local-setup.md` — 移除 Qdrant/Embedding 前置条件和功能表行
  5. `docs/modules/HybridSearch/` 6 个文件 — 添加"已移除"标注
  6. 其他 modules 文档 — 标注 Qdrant/Embedding 相关描述为"已移除"
- **新增或修改的测试**：修改 4 个测试文件（移除 Qdrant/HybridSearch 相关测试用例）
- **实际执行的验证命令**：
  - `dotnet build` → 0 错误 0 警告
  - `dotnet test` → 93 通过，0 失败
- **结果摘要**：完整移除 Qdrant/Embedding/HybridSearch 相关代码、测试、配置和文档。项目仅保留 OpenSearch BM25 精确搜索。构建和测试全部通过。

## 5. 正式文档同步记录

| 文档路径                                                                             | 更新原因                                                 | 更新摘要                                                                   |
| -------------------------------------------------------------------------------- | ---------------------------------------------------- | ---------------------------------------------------------------------- |
| `backend/ruoyu.docretrieval/docs/overview/Design.md`                             | 原技术栈表 "OpenSearch 1.8" 歧义（实为 NuGet 包版本）              | 改为 "OpenSearch 2.19 (Docker) / OpenSearch.Net 1.8 (NuGet)"，区分服务器与客户端版本 |
| `backend/ruoyu.docretrieval/docs/development/local-setup.md`                     | 补充 OpenSearch 推荐版本信息                                 | 前置条件表 OpenSearch 行增加推荐 Docker 镜像版本 2.19.5 及兼容性说明                       |
| `backend/ruoyu.docretrieval/docs/overview/Design.md`                             | 技术栈表 Qdrant 版本描述不完整                                  | 改为 "Qdrant 1.18 (Docker) / Qdrant.Client 1.18 (NuGet)"，区分服务器与客户端版本     |
| `backend/ruoyu.docretrieval/docs/development/local-setup.md`                     | 补充 Qdrant 推荐版本信息                                     | 前置条件表 Qdrant 行增加推荐 Docker 镜像版本 v1.18.2 及兼容性说明                          |
| `backend/ruoyu.docretrieval/src/Service/Ruoyu.Study.DocRetrieval.Service.csproj` | Qdrant.Client NuGet 包升级                              | 1.12.0 → 1.18.1                                                        |
| `backend/ruoyu.docretrieval/src/Host/Ruoyu.Study.DocRetrieval.Host.csproj`       | Qdrant.Client NuGet 包升级                              | 1.12.0 → 1.18.1                                                        |
| `backend/ruoyu.docretrieval/docs/overview/Design.md`                             | ITEM-04: 移除 Qdrant 技术栈行和 QdrantService 引用            | 移除 Qdrant 相关技术栈描述                                                      |
| `backend/ruoyu.docretrieval/docs/overview/SystemContext.md`                      | ITEM-04: 移除 Qdrant/SiliconFlow 依赖和 HybridSearch RPC  | 移除外部依赖和 RPC 描述                                                         |
| `backend/ruoyu.docretrieval/docs/overview/Integration.md`                        | ITEM-04: 移除 Qdrant/SiliconFlow 集成行和 HybridSearch RPC | 移除集成描述                                                                 |
| `backend/ruoyu.docretrieval/docs/development/local-setup.md`                     | ITEM-04: 移除 Qdrant/Embedding 前置条件和功能表行               | 移除不再需要的部署前置条件                                                          |
| `backend/ruoyu.docretrieval/docs/modules/HybridSearch/` (6 files)                | ITEM-04: 标注"已移除"                                     | 添加功能已移除标注                                                              |
| `backend/ruoyu.docretrieval/src/Contract/Protos/docretrieval.proto`              | ITEM-04: 移除 HybridSearch RPC                         | 仅保留 ExactSearch RPC                                                    |
| `backend/ruoyu.docretrieval/src/Host/Program.cs`                                 | ITEM-04: 移除 Qdrant/Embedding DI 注册和初始化               | 仅保留 OpenSearch 配置和初始化                                                  |
| `backend/ruoyu.docretrieval/src/Host/appsettings.json`                           | ITEM-04: 移除 Qdrant 和 Embedding 配置节                   | 仅保留 OpenSearch 配置                                                      |

## 6. 未完成或暂时跳过的输入项

无。

## 7. 风险与待人工复核事项

1. **OpenSearch 3.x 升级待评估**：当前 `OpenSearch.Net 1.8.0` 客户端的兼容矩阵未覆盖 OpenSearch 3.x。如需升级到 3.7.0，建议：
   - 等待 opensearch-net 客户端发布适配 3.x 的版本
   - 或在测试环境验证 `OpenSearchLowLevelClient` 与 OpenSearch 3.x 的实际兼容性     你根据搜索的资料确定最合适的版本就行，不用我来审核
2. **部署环境验证**：脚本版本已更新，需在实际部署环境拉取新镜像并启动容器验证：
   - `docker pull opensearchproject/opensearch:2.19.5   这个我会来做，不用审核`
3. **数据卷兼容性**：
   - OpenSearch: 2.19.1 → 2.19.5 补丁升级通常无数据兼容问题，建议升级前备份 `scripts/1.env/data/` 目录    这个事情我会处理，不用审核
4. **Qdrant 启动脚本处理**：`scripts/1.env/start-qdrant.sh` 已不再需要（Qdrant 已从项目中移除），建议：
   - 删除该脚本，或
   - 在脚本头部添加废弃标注（`# DEPRECATED: Qdrant has been removed from the project`）   可以删除相关信息
5. **语义搜索未来恢复路径**：如未来需要语义搜索功能，推荐方案为：
   - 使用 OpenSearch 自身的 k-NN 插件（无需额外部署 Qdrant）
   - Embedding API 可选用 SiliconFlow 或其他兼容 OpenAI 接口的服务
   - 需重新引入 `HybridSearchAsync` 方法，但建议基于 OpenSearch k-NN 而非 Qdrant 实现    先全部干掉相关部分，后面再考虑

