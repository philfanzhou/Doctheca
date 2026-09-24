# DocumentSearch — 设计说明 (DESIGN)

## 设计决策

### 索引与搜索为何同服务

索引写入与精确搜索共享同一套 OpenSearch mapping（`BuildIndexBody`），强耦合：
- 查询字段（`text` / `text.exact` / `file_name` / `subject` / `grade` / `year`）与索引字段必须严格一致。
- `ParseSearchResponse` 读取的字段名（`block_id` / `file_name` / `page_number`）与 `IndexParseBlocksAsync` 写入的字段名同源。
- 拆分两个服务会导致 mapping 变更时需跨服务同步，增加不一致风险。

因此合并到同一模块文档（本模块），映射与查询逻辑集中维护。

### best-effort 索引策略

所有 OpenSearch 写操作（索引/删除/元数据同步）均为 best-effort：
- 失败仅记录 Warning 日志，不抛异常给外层。
- **理由**：搜索是辅助能力，不应阻塞/破坏核心解析链路。解析结果持久化到数据库后，即使 OpenSearch 索引失败，数据不丢，仅搜索暂时不可用。
- Worker 与端点层统一用 `try/catch + LogWarning` 包裹。

### 搜索降级策略

`SearchDomainService` 作为 `ISearchIndexService` 的薄封装层，统一处理异常：
- 捕获**任意**异常（不限特定类型），LogWarning 后返回空结果。
- **理由**：HTTP 搜索请求不应因 OpenSearch 故障而返回 500；调用方（前端/下游）收到空结果可正常渲染"无结果"。
- 降级逻辑在领域层（`SearchDomainService`），而非 HTTP 端点层，保证复用。

### 查询构建可测试性

`OpenSearchIndexService` 的 `OpenSearchLowLevelClient` 在构造函数中 `new` 创建，难以注入 Mock。因此将纯逻辑提取为 `internal static` 方法：
- `BuildIndexBody()` — 索引 mapping/settings
- `BuildSearchBody(query, phrase, filter, pageSize, pageToken, logger)` — 搜索请求体
- `ParseSearchResponse(responseJson, phrase, pageSize)` — 响应解析

这些方法无外部依赖，单元测试可直接调用验证字段/逻辑正确性（通过 `InternalsVisibleTo` 暴露给测试程序集）。

## 第 2 代演进：基于 minerU v1 JSON 的 block 级检索

> **演进方式：并入 V1 索引 / V1 endpoint / V1 前端**—— 不新建平行 endpoint、不新建 `BlockXxx` 独立模型类、不新建独立的 `BlockSearchPage.vue` 平行页。原因（来自 @user 设计审查）：管理后台的"block 级检索"与现有的"精确关键词检索"共享同一 OpenSearch 索引、同一 `document_parse_blocks` 表、同一 `StructaDocParseWorker` 入口、同一 `SearchDomainService` 降级链路、同一 doclibrary 自带前端——另开一条平行路径等于维护两套检索。

### minerU v1 block 字段来源与分层

> minerU（前 magic-pdf / PDF-Extract-Kit）v1 `content_list.json` 的真实 schema 在本环境无法在线校验（github.com / pypi.org / opendatalab.github.io 均被网关拦截，WebSearch 返回通用模板），但基于**项目既有代码实际读取路径 + MinerU 公开枚举共识**足以落地演进：

**维度 1 — V1 已有字段，直接复用（同源，无需 alias 双写）**：
- `block_type` ← minerU `type`（`DocumentParseBlockService.ParseBlock:95-100`）
- `page_number`（= `block.PageId`）← minerU `page_id`（`DocumentParseBlockService.cs:104-107`）
- `text`（= `block.TextContent`）← minerU `text|content|body` 优先级抽取（`DocumentParseBlockService.cs:151`）

这 3 个维度**在新演进里不新增任何 mapping 字段**，仅在 V1 endpoint 上**暴露它们作为过滤参数**（目前 V1 mapping 已索引 `block_type` / `page_number` / `text`，但 endpoint `GET /admin/documents/search` 尚未开放按 `blockType` / `pageNumber` 过滤）。

**维度 2 — minerU 官方权威 schema（已 minerU 官方文档确认）**：
minerU 输出结构权威来源：minerU 官方文档 `docs/zh/reference/output_files.md`（`pipeline` 后端 + `VLM` 后端两个版本，结构略有差异）。read via `curl https://raw.githubusercontent.com/opend_lab/MinerU/master/docs/zh/reference/output_files.md`（28KB，867行）。

### minerU `content_list.json` block type 权威枚举

#### pipeline 后端（已验证：`DocumentParseBlockService.ParseBlock` 对接本格式）

```
通用字段: type, bbox [x0,y0,x1,y1] (0-1000 归一化), page_idx (0-based)

一级 block type:            子类型（通过二级 block/sub_type 区分）
├── text                     text / title / index / list / interline_equation
├── image                    image_body, image_caption, image_footnote
├── table                    table_body, table_caption, table_footnote, table_body(<html>)
├── chart                    chart_body, chart_caption, chart_footnote
└── discarded_blocks         header, footer, page_number, aside_text, page_footnote

二级文本片段 (span) 字段:    type (text/image/table/chart/inline_equation/interline_equation), bbox, content | image_path
衍生字段（首版 V2 索引）:     text_level (0=正文,1=h1,2=h2..., 非标题则缺失), sub_type, caption
```

#### VLM 后端（官方文档确认：整体结构更扁平）

```
顶层字段: type, bbox [x0,y0,x1,y1] (0-1 百分比 ⚠️), content, angle (0/90/180/270), score, text_format (latex/markdown/none)

type 枚举 (完整): text, title, equation, image, image_caption, image_footnote,
                  table, table_caption, table_footnote, chart, chart_caption, chart_footnote,
                  code, code_caption, algorithm, phonetic, ref_text, list (sub_type: text/ref_text),
                  header, footer, page_number, aside_text, page_footnote

新增字段 vs pipeline: text_level, text_format, sub_type (code 区分 code/algorithm; list 区分 text/ref_text), list_items
```

⚠️ **关键差异**：VLM 后端 `bbox` 是 **0-1 百分比**，pipeline 后端 `bbox` 是 **0-1000 归一化**。写入 OpenSearch 前由 `DocumentParseBlockService.ParseBlock` 统一归一化到 0-1000（pipeline 惯例），避免前端/混合索引场景坐标歧义。

`[权威] 以上 minerU 官方 schema 来自 docs/zh/reference/output_files.md；具体枚举以该文档 minerU 最新版本为准。`

**维度 3 — 真正需要*新增*映射到 OpenSearch 的字段**（V1 索引目前没有对应列）：

| minerU 字段 | 类型 | 用途 | 是否进 mapping |
|------------|------|------|--------------|
| `bbox` `[x0,y0,x1,y1]` | float[4] | 页面坐标（top-left 原点）→ 支持按视觉区域过滤 | 是（`float[]` + 独立 `x0/y0/x1/y1` 便于范围检索） |
| `score` | float | MinerU 置信度 → 支持"高置信 block"过滤（排查解析质量问题） | 是（float） |
| `image_path` / `img_path` | string | 仅 image/figure 块有效 → 是否携带图片的布尔索引 | 是（衍生 `has_image` bool，原始 `image_path` 进 `block_data`） |
| `sub_type` | string | minerU 官方字段，区分 caption/body/footnote 二级分类（如 `table_caption`、`code`、`algorithm`、`text`、`ref_text`）→ 支持"所有图注"、"所有代码块"等过滤 | 是（keyword） |
| `text_level` | int | minerU 官方字段，标题层级（`0` = 正文, `1` = h1, `2` = h2...；非标题文本缺失）→ 支持"所有一级标题 block"过滤 | 是（integer，非标题文本索引为 `-1`） |
| `text_format` | string | minerU VLM 后端特有字段（`latex` / `markdown` / `none`）→ 支持"行间公式 block"过滤 | 是（keyword；pipeline 后端无此字段时索引空字符串） |
| `caption` | text | 派生字段：将 `image_caption` / `table_caption` / `chart_caption` / `code_caption` 等 caption 文本拼接为可检索文本 → 使关键词搜索命中"图注/表注" | 是（text, english_custom 分析器） |
| `block_data` 整块原文 | object | 管理界面"查看原始 minerU JSON"详情 | 是（嵌套 `_meta.block_data`，`enabled:false` 仅存不索引） |

> 其他 minerU 字段（`chars`、`position`、`layout_width`、`images`、`table_html`、`angle`、`block_tags`、`content_tags`、`list_items`、`code_body`、`code_language`、`table_body` 等）首版**不进入 mapping**——已入库到 `document_parse_blocks.block_data`（jsonb），管理界面需要时通过 `GET /admin/document-files/{id}` 展示，或延至 facet 需求明确后再追加。

### 演进 V1 的具体改法（合并索引 + 扩展 endpoint）

**方案 P（原平行 V2，弃用）**：新增 `BlockSearchEndpoints` + `BlockResultModel` + `BlockSearchPage.vue` 独立类/端点/页 → 维护两套检索。

**方案 Q（演进 V1，选用）**：

| 层 | 变更 | 复用 |
|----|------|------|
| OpenSearch 索引映射 | V1 `BuildIndexBody` 追加 `bbox`/`x0 y0 x1 y1`/`score`/`has_image`/`_meta.block_data` | 同一索引、同一 `_id` |
| 索引写入 Worker | `StructaDocParseWorker.IndexBlocksToSearchAsync` 在同一 bulk 追加新字段 | 同一 best-effort 入口 |
| 领域服务 | `SearchDomainService.ExactSearchAsync` 扩展 + 新增参数；**不新增方法**，只是同一方法里增加 filter/返回字段分支 | 委托 `ISearchIndexService` 的扩展签名 |
| 端点 | `DocumentSearchEndpoints.Search` **扩展**：新增可选入参 `blockType`/`pageNumber`/`hasImage`，返回的 `SearchResultModel` 追加 `BlockData`/`Bbox`/`Score` 字段（新增字段 optional，对老前端兼容） | 同一 `GET /admin/documents/search` |
| 前端 | `SearchPage.vue` 加"高级筛选"抽屉（minerU 字段过滤）+ 结果行展开显示 `blockData` | 同一页面，不并列 |

**决策**：选用方案 Q（演进 V1）。

理由：
- 索引/写入/降级/分页完全复用；维护工作量接近最小。
- 结果契约向后兼容：新增字段 `BlockData`/`Bbox`/`Score` 在 `SearchResultModel` 是 optional；不传/不展示的前端（老 Ruoyu.Admin 如有引用）不受影响。
- 管理界面入口仍在 doclibrary 自带前端；不扩大 Ruoyu.Admin 边界。
- minerU `type` 与 `block_type` 同源、`page_id` 与 `page_number` 同源、`text|content|body` 与 `text_content` 同源 → 维度 1 零新增映射成本。

### `block_data` 回挂策略（同平行 V2 的 B 方案，精简后保留）

| 方案 | 写入放大 | 查询延迟 | 取舍 |
|------|---------|---------|------|
| A. 平铺顶层 string 字段 | 中 | 低 | 弃用 |
| **B. 嵌套 `_meta.block_data`（`enabled:false`）** | **低（仅存不索引）** | **低（`_source` 回挂）** | **选用** |
| C. 拆独立索引 | 高 | 中 | 弃用 |
| D. 每次二次回查 DB | 0 写入 | 高（二次往返） | 弃用 |

### minerU type 枚举进入 endpoint 的约定

V1 endpoint 的新参数 `blockType` 接受**字符串**（自由文本），服务端用 `term` 精确匹配 `block_type` 索引字段。不强制 enum 校验——因为解析侧升级会引入新类型（存量为 MinerU 类型，新记录为 StructaDoc canonical 类型），校验白名单会成为负担。前端下拉框基于**历史数据聚合**（运维可在"块类型分布"侧边栏看到当前值域）。

`[说明] 如后续 minerU 类型枚举频繁变动，可在前端下拉维护一个静态候选列表；后端仍保持字符串透传。`

## 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── SearchResultModel.cs               # 搜索结果模型（第 2 代演进追加 BlockData/Bbox/Score）
│   │   │   ├── SearchFilterModel.cs               # 搜索过滤模型（第 2 代演进追加 BlockType/PageNumber/HasImage）
│   │   │   ├── SearchMatchType.cs                 # 匹配类型常量（不变）
│   │   │   └── OpenSearchOptions.cs               # OpenSearch 配置节（不变）
│   │   ├── Services/
│   │   │   ├── ISearchDomainService.cs            # 搜索领域服务接口（ExactSearchAsync 扩展签名）
│   │   │   └── SearchDomainService.cs             # 搜索领域服务实现（同一 ExactSearchAsync 内扩展 filter）
│   │   └── Repositories/
│   │       ├── ISearchIndexService.cs             # 搜索索引接口（追加 minerU 字段索引）
│   │       └── IDocumentParseBlockRepository.cs   # block 仓储接口（不变，不设 GetPagedAsync 新方法）
│   ├── Service/
│   │   ├── Endpoints/
│   │   │   └── DocumentSearchEndpoints.cs         # GET /admin/documents/search（第 2 代演进扩展入参与响应）
│   │   ├── OpenSearch/
│   │   │   ├── OpenSearchIndexManager.cs          # 索引生命周期管理（创建/删除/版本检查）
│   │   │   ├── OpenSearchQueryBuilder.cs          # 搜索请求体构造（BuildSearchBody）
│   │   │   ├── OpenSearchResponseParser.cs        # 搜索响应解析（ParseSearchResponse）
│   │   │   └── OpenSearchJsonHelper.cs            # OpenSearch JSON 字段安全读取辅助方法
│   │   ├── OpenSearchIndexService.cs              # OpenSearch 索引服务 facade（组合上述类，实现 ISearchIndexService）
│   │   └── StructaDocParseWorker.cs               # 解析完成后索引（best-effort，追加 minerU 字段）
│   └── Host/
│       ├── frontend/                              # doclibrary 自带前端
│       │   ├── src/pages/
│       │   │   └── SearchPage.vue                 # 搜索页（第 2 代演进加"高级筛选"抽屉 + blockData 展开）
│       ├── appsettings.json                       # OpenSearch 配置（不变）
│       └── Program.cs                             # DI 注册（不变）
└── src/Tests/Ruoyu.Study.DocLibrary.Tests/
    ├── OpenSearchIndexServiceTests.cs             # 追加 minerU 字段映射/查询/回挂用例
    └── SearchDomainServiceTests.cs                # 追加 minerU filter 透传 + 回挂降级用例
```

## 关键接口签名

### ISearchIndexService

见 [02-SPEC.md §1.1](./02-SPEC.md#11-isearchindexservice)。

### 注入的依赖

```csharp
// OpenSearchIndexService 构造函数
public class OpenSearchIndexService : ISearchIndexService, IDisposable
{
    public OpenSearchIndexService(
        IOptions<OpenSearchOptions> options,
        IServiceProvider serviceProvider,
        ILogger<OpenSearchIndexService> logger)
    {
        // ...
    }
}
```

- `IOptions<OpenSearchOptions>`：OpenSearch 配置。
- `IServiceProvider`：用于创建 scope 解析 `IDocumentParseBlockRepository`（scoped 生命周期）。
- `ILogger<OpenSearchIndexService>`：结构化日志。
- `OpenSearchLowLevelClient` 在构造函数中 `new` 创建；服务实现 `IDisposable`，`Dispose()` 调用 `(_client as IDisposable)?.Dispose()`，由 DI 容器在 Singleton 销毁时释放。当前引用的 `OpenSearch.Net` 1.8.0 包中 `OpenSearchLowLevelClient` 未公开 `Dispose()` 方法，因此采用防御式转换；如未来版本实现 `IDisposable`，释放将自动生效。

```csharp
// SearchDomainService 构造函数
public SearchDomainService(
    ISearchIndexService searchIndexService,
    ILogger<SearchDomainService> logger)
```

- `ISearchIndexService`：**非 nullable**，由 DI 容器保证注入（注册为 Singleton）。
- `ILogger<SearchDomainService>`：降级时 LogWarning。

### DI 注册（`Program.cs`）

```csharp
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"));
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
```

## 数据流

### 索引写入流程

```
StructaDocParseResultSync.SyncAsync
  → parseService.UpdateStatusAsync(Parsed)
  → IndexBlocksToSearchAsync (best-effort, try/catch LogWarning)
      → scopeProvider.GetRequiredService<ISearchIndexService>()
      → searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year)
          → 创建 scope → 解析 IDocumentParseBlockRepository
          → GetByParseIdAsync(parseId)
          → 过滤空 text_content → 构建 bulk → BulkAsync → 记日志
  → AnalyzeMetadataIfMissingAsync (best-effort)
      → LLM 分析 → fileService.UpdateMetadataAsync
      → searchIndexService.UpdateDocumentFileMetadataAsync (同步索引)
```

- 大文件分块解析（`PersistMergedChunkResultsAsync`）仅在 `status == Parsed` 时索引。
- 失败状态（`Failed`）不索引。

### 索引删除流程

```
DELETE /admin/document-parses/{parseId}
  → DocumentParseEndpoints.DeleteDocumentParse
  → parseService.DeleteParseAsync(parseId)
  → searchIndexService.DeleteParseIndexAsync(parseId) (best-effort)

DELETE /admin/document-files/{id}
  → DocumentFileEndpoints.DeleteDocumentFile
  → fileService.DeleteAsync(id)
  → OSS 清理 (best-effort)
  → searchIndexService.DeleteDocumentFileIndexAsync(id) (best-effort)
```

### 元数据同步流程

```
PUT /admin/document-files/{id}/metadata
  → DocumentFileEndpoints.UpdateDocumentFileMetadata
  → fileService.UpdateMetadataAsync(id, subject, grade, year)
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)

LLM 自动分析（AnalyzeMetadataIfMissingAsync）
  → fileService.UpdateMetadataAsync
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)
```

### 搜索流程

```
GET /admin/documents/search
  → DocumentSearchEndpoints.Search
  → 参数校验（query 空/过长 → 400）
  → pageSize = Math.Min(Math.Max(pageSize, 1), 100)
  → 构造 SearchFilterModel（仅当 subject/grade/year/documentTitle 任一非空）
  → searchService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)
      → _searchIndexService.ExactSearchAsync(...)
          → BuildSearchBody → _client.SearchAsync → ParseSearchResponse
      → 异常时：LogWarning → 返回空结果
  → 构建 JSON 响应（results / totalCount / nextPageToken）
```

### 启动初始化流程（`Program.cs`）

```
1. DatabaseInitializer.InitializeAsync(dbContext, loggerFactory)  — 数据库初始化（无 EF Core Migration）
2. searchIndexService.EnsureIndexAsync() (best-effort, 失败不阻塞启动)
   → EnsureIndexExistsAsync
       → 检查索引存在
           ├─ 不存在 → CreateAsync（BuildIndexBody + _meta.mapping_version=CurrentMappingVersion）
           └─ 存在 → 读取索引 _meta.mapping_version
               ├─ 版本缺失或 < CurrentMappingVersion → 删除索引 → CreateAsync（重建为新版本 mapping）
               │     ⚠️ 重建会清空所有已索引文档，需重新触发解析才能恢复索引数据
               │     日志：[WRN] OpenSearch index {name} has outdated mapping (expected=v, actual=v_old), recreating
               └─ 版本匹配 → 跳过（正常启动）
```

#### 索引 mapping 版本管理（2026-07-10 补充）

OpenSearch 索引的 mapping 一旦创建就**不可在线修改**（字段类型变更需重建索引）。`EnsureIndexAsync` 只检查索引是否存在，存在就跳过——这意味着代码升级 mapping 后，旧索引不会被自动更新，查询会因 mapping 不一致而 400。

**解决方案**：在索引 `_meta` 中写入 `mapping_version`，启动时对比版本号，版本不匹配则删除并重建索引。

- `CurrentMappingVersion` 常量定义在 `OpenSearchIndexService`，每次 `BuildIndexBody` 的 mapping 变更时递增
- `BuildIndexBody` 的 `_meta` 字段追加 `mapping_version`
- `EnsureIndexExistsAsync` 读取索引 `_meta.mapping_version`，缺失或低于当前版本则删除重建
- 重建是 best-effort：删除失败 → LogWarning 不阻塞启动；删除成功后创建失败 → LogWarning 不阻塞启动

> **版本历史**：
> - v1：初始 mapping（V1 检索能力）
> - v2：minerU Gen-2 演进（追加 x0/y0/x1/y1/score/has_image/sub_type/text_level/text_format/caption/_meta.block_data）

> **历史背景**：2026-07-10 部署 minerU Gen-2 后，旧索引（v1 mapping）与新查询代码不兼容，搜索请求全部 400。原 `EnsureIndexAsync` 只检查索引存在性，不检查 mapping 版本，导致旧索引无法自动更新。引入版本号机制后，服务重启即自动检测并重建一次。

## 错误处理策略

| 场景 | 处理 |
|------|------|
| OpenSearch 索引失败 | Worker 捕获异常 → LogWarning，解析流程继续 |
| OpenSearch 删除失败 | 端点捕获异常 → LogWarning，删除流程继续 |
| OpenSearch 搜索失败 | `SearchDomainService` 捕获异常 → LogWarning，返回空结果 |
| OpenSearch 元数据同步失败 | 调用方捕获异常 → LogWarning，不阻塞 |
| blocks 为空 | LogWarning 返回，不抛异常 |
| `text_content` 为空 | 跳过该 block（不索引空文本） |
| `pageToken` 非法 Base64 | LogDebug，从第一页开始 |
| HTTP 参数校验失败 | 返回 400 + 错误码 |

### 搜索失败诊断信息（2026-07-10 补充）

`ExactSearchAsync` 在 OpenSearch 返回非 200 时，异常消息必须包含**响应体**（OpenSearch 的错误 JSON），否则仅凭 status code 无法定位 400/500 的根因。

```csharp
// 正确：异常消息包含响应体
if (!response.Success || response.HttpStatusCode != 200)
{
    var errorBody = response.Body != null ? Encoding.UTF8.GetString(response.Body) : "(empty)";
    throw new InvalidOperationException(
        $"OpenSearch query failed, status code: {response.HttpStatusCode}, response: {errorBody}, query: {json}");
}
```

同时以 Debug 级别记录发送的查询 JSON，便于复现：

```csharp
_logger.LogDebug("OpenSearch search request: index={Index}, body={Body}", indexName, json);
```

> **历史背景**：2026-07-10 部署后发现搜索请求全部返回空结果，日志仅有 `OpenSearch query failed, status code: 400`，无法定位根因。根因是异常消息缺少 OpenSearch 响应体。修复后异常消息包含 `response` 和 `query` 字段，`SearchDomainService` 的 LogWarning 即可输出完整诊断信息。

## 依赖的外部模块接口

| 依赖 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentParseBlockRepository` | 读取 parse 的 blocks（索引数据源） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IDocumentParseBlockRepository.GetByParseIdAsync` | 通过与 V1 同一查询取 blocks，不新增仓储方法 | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IOptions<OpenSearchOptions>` | OpenSearch 配置（Url / IndexName） | `Ruoyu.Study.DocLibrary.Domain.Models` |
| OpenSearch 2.x | 索引存储与搜索 | 外部服务（HTTP） |
| `StructaDocParseWorker` | 索引写入复用的 best-effort 入口（第 2 代 minerU 字段在同一 bulk 追加） | `Ruoyu.Study.DocLibrary.Service` |

> 第 2 代演进**不引入**新外部依赖（无新 NuGet 包、无新数据库表、无新 OSS 存储、无第三方 minerU 客户端变更）。minerU v1 block schema 中 V1 索引未覆盖的字段（`chars`/`position`/`layout_width` 等）以 `block_data` jsonb 入库，管理界面通过现有 `GET /admin/document-files/{id}` 查看，不构成新依赖倒置。

## 第 2 代演进数据流

### 索引写入演进（与 V1 同一 bulk，追加 minerU 字段）

```
StructaDocParseWorker.IndexBlocksToSearchAsync (best-effort)
  → scopeProvider.GetRequiredService<ISearchIndexService>()
  → searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year)
      → 创建 scope → 解析 IDocumentParseBlockRepository
      → GetByParseIdAsync(parseId)
      → 过滤空 text_content → 构建 bulk（V1 字段 + 第 2 代 minerU 字段，同一 _id）
          V1 字段：parse_id, document_file_id, file_name, subject, grade, year,
                   page_number, block_id, block_type, text, sort_index, image_id, created_at
          第 2 代 minerU 新增：x0, y0, x1, y1（bbox 分量，float），score（float），
                                has_image（bool），_meta.block_data（object, enabled:false）
      → BulkAsync → 记日志
```

> minerU 新增字段数据来源：`block.BlockData`（jsonb 原文）在 `DocumentParseBlockService.ParseBlock` 解析阶段已落地到实体字段（ImageId），`bbox`/`score` 由解析服务反序列化后写入 block 实体；此处索引端不二次解析 minerU JSON。

### 搜索演进（V1 endpoint 扩展）

```
GET /admin/documents/search
  → DocumentSearchEndpoints.Search
  → 参数校验（query 空/过长 → 400；若同时所有 minerU filter 均为空则按 V1 仅 keyword 路径执行）
  → pageSize = Math.Min(Math.Max(pageSize, 1), 100)   （V1 100 不变；带 minerU filter 时建议 ≤ 50，前端引导）
  → 构造 SearchFilterModel（V1 原有 4 项 + 第 2 代追加 BlockType/PageNumber/HasImage）
  → searchService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)
      → _searchIndexService.ExactSearchAsync(...)
          → BuildSearchBody（keyword 走 must；blockType/pageNumber/hasImage 走 filter）
                         → _client.SourceIncluding(new[] { "_meta.block_data", ... })
          → _client.SearchAsync
          → ParseSearchResponse（字段映射 + 从 _source 抽取 minerU 字段 + 回挂 BlockData）
      → 异常时：LogWarning → 返回空结果
  → 构建 JSON 响应：results（含新增 BlockData/Bbox/Score 字段，可选）/ totalCount / nextPageToken
```

## 可测试性设计

- **纯逻辑提取**：`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 为 `internal static`，无 IO 依赖，可直接单元测试。
- **接口隔离**：`ISearchIndexService` / `ISearchDomainService` 通过构造函数注入，`SearchDomainService` 可用 Mock 验证。
- **降级可验证**：Mock `ISearchIndexService.ExactSearchAsync` 抛异常，验证 `SearchDomainService` 返回空结果 + LogWarning。
- **启动初始化 best-effort**：`EnsureIndexAsync` 失败不阻塞服务启动，仅记日志。
- **第 2 代 minerU filter 纯逻辑可测**：`BuildSearchBody` 在现有 `internal static` 测试基础上追加 filter 构造断言；`ParseSearchResponse` 在现有 `internal static` 测试基础上追加 minerU 字段回挂断言（`BlockData`/`Bbox`/`Score` 抽取）。**不新增**独立的 `BuildBlock*` / `ParseBlock*` 方法，避免双重维护。
- **第 2 代降级可验证**：复用现有 `ISearchIndexService.ExactSearchAsync` 抛异常的 Mock 用例路径，验证 minerU filter 仍走 `SearchDomainService` 降级返回空结果 + LogWarning。
