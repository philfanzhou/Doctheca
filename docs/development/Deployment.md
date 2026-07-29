# Deployment — 部署与运维

## 服务端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 5012 | HTTP | Web 管理界面 + 搜索 API + 静态文件 + Health Check |

端口可在 `appsettings.json` 的 `Endpoints:Http` 中覆盖。

## Consul 接入

`ruoyu.doclibrary` 现在固定接入 Consul，通过 `ruoyu.common` 下的共享 Consul 代码读取共享配置并注册服务。

启动脚本 `start.sh` 只保留以下几类参数：

- `CONSUL_HTTP_ADDR`
- `CONSUL_TOKEN`
- `Endpoints:Http`
- `ConnectionStrings:Default` 中的数据库名部分
- `OpenSearch:IndexName`
- `LlmDocumentAnalysis:*`
- `MinerU:*`
- `IdentityService:Authority/AppId/AppSecret`
- `Authentication:CookieSecure`
- `InternalAuth:QuestionBankKey`

以下配置迁入共享 Consul KV：

- `config/ruoyu/shared.json`
  - `PostgreSql:Host`
  - `PostgreSql:Port`
  - `PostgreSql:Username`
  - `PostgreSql:Password`
  - `Oss:*`
  - `OpenSearch:Url`
  - `Loki:Uri`
- `config/ruoyu/service-endpoints.json`
  - `IdentityService:Authority`
  - `IdentityService:Audience`
  - `IdentityService:RequireHttpsMetadata`
- `config/ruoyu/serilog.json`
  - `Serilog:MinimumLevel:*`

> `Database:Name`、`OpenSearch:IndexName`、`LlmDocumentAnalysis:*`、`MinerU:*` 都属于 DocLibrary 私有配置，不进入共享 KV。

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `CONSUL_HTTP_ADDR` | `host.docker.internal:8500` | Consul HTTP API 地址 |
| `CONSUL_TOKEN` | （空） | Consul ACL token（启用 ACL 时必需） |
| `CONSUL_KV_PREFIX` | `config/ruoyu` | Consul 共享 KV 前缀 |
| `CONSUL_CACHE_DIR` | `./data/consul` | Consul 本地缓存目录 |
| `USE_LOCAL_OSS` | （未设置） | 设为 `1` 使用本地文件系统存储代替 S3 |
| `OSS_LOCAL_PATH` | `data/oss` | 本地文件存储目录（仅在 `USE_LOCAL_OSS=1` 时使用） |
| `IDENTITY_AUTHORITY` | `http://ruoyu-identity:5002` | `start.sh` 映射到 `IdentityService__Authority` |
| `IDENTITY_APP_ID` | （空） | 可选；映射到 `IdentityService__AppId`，必须与 AppSecret 同时设置 |
| `IDENTITY_APP_SECRET` | （空） | 可选；映射到 `IdentityService__AppSecret`，不得写入日志或源码 |
| `DOCLIBRARY_COOKIE_SECURE` | `false` | 映射到 `Authentication__CookieSecure`；HTTPS 生产部署必须设为 `true` |
| `DOCLIBRARY_QUESTIONBANK_KEY` | 无默认值 | 映射到 `InternalAuth__QuestionBankKey`；`start.sh` 缺失时拒绝启动 |

直接运行 Host 时也可使用 .NET 分层配置名
`IdentityService__*`、`Authentication__CookieSecure` 和
`InternalAuth__QuestionBankKey`；上述大写变量是 `start.sh` 的部署入口。

## 下游依赖

| 依赖 | 端口 | 用途 |
|------|------|------|
| PostgreSQL | 5432 | 主数据库（`ruoyu_study_doclibrary`） |
| OpenSearch | 9200 | 全文检索索引 |
| MinIO / SeaweedFS | 8333 | 对象存储（S3 兼容） |
| Consul | 8500 | 共享配置读取与服务注册 |
| Loki | 3100 | 日志聚合（通过 Consul `Loki:Uri` 配置） |
| QuantumZhou.Identity | 5002 | 管理员登录、Token刷新/撤销、OIDC discovery/JWKS |

QuestionBank 通过 `X-DocLibrary-Service-Key` 调用三个 `/internal/question-bank/*` GET。DocLibrary 与 QuestionBank 必须配置相同的高熵服务密钥；密钥不得进入前端构建产物。

## 管理员认证配置

最小配置：

```json
{
  "IdentityService": {
    "Authority": "http://ruoyu-identity:5002",
    "Issuer": "QuantumZhou.Identity",
    "Audience": "QuantumZhou.microservices",
    "RequireHttpsMetadata": false
  },
  "Authentication": {
    "CookieSecure": false
  },
  "InternalAuth": {
    "QuestionBankKey": "<deployment secret>"
  }
}
```

Identity `POST /api/auth/token` 支持不带 AppId/AppSecret 的密码登录，bootstrap 管理员角色注入不依赖 portal callback。因此 DocLibrary 接入不要求修改 Identity `AdminBootstrap`。如部署要求为登录请求记录应用归属，可单独注册 DocLibrary App并同时配置 AppId/AppSecret。

生产环境必须在浏览器与 DocLibrary 之间使用 HTTPS，并设置 `Authentication:CookieSecure=true`。`RequireHttpsMetadata=false` 只适用于容器内 HTTP Authority 或本地开发。

Cookie 名称和路径：

| Cookie | Path | 用途 |
|--------|------|------|
| `doclibraryAccessToken` | `/admin` | 管理 API JWT |
| `doclibraryRefreshToken` | `/admin/auth` | 刷新与登出 |

两个 Cookie 均为 HttpOnly、SameSite=Strict，不向前端 JavaScript 暴露。

## 日志配置

DocLibrary 使用 Serilog 替代原生 Microsoft.Extensions.Logging，双写到 Console + Grafana Loki。接入方式与 Identity 服务完全一致。

### Serilog 配置

服务通过 `builder.Host.UseAgentSerilog("Ruoyu.Study.DocLibrary")` 配置 Serilog。`appsettings.json` 中的 `Logging` 节仅保留给未走 Serilog 的少量运行时组件，**业务日志级别以 Serilog 配置为准**。

| 配置键 | 默认值 | 说明 |
|--------|--------|------|
| `Serilog:MinimumLevel:Default` | Information | 默认日志级别 |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | Warning | ASP.NET Core 日志级别 |
| `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command` | Warning | EF Core 日志级别 |
| `Serilog:WriteTo:0:Name` | Console | 控制台 Sink（数组下标 0） |
| `Serilog:WriteTo:1:Name` | GrafanaLoki | Loki Sink（数组下标 1） |
| `Serilog:WriteTo:1:Args:uri` | http://ruoyu-loki:3100 | Loki 地址（最终由 `Loki:Uri` 覆盖） |
| `Serilog:WriteTo:1:Args:labels:0:key` | service | Loki 标签键 |
| `Serilog:WriteTo:1:Args:labels:0:value` | Ruoyu.Study.DocLibrary | Loki 标签值（service 标签） |

### 日志 Enricher

每条日志自动携带以下字段：

| 字段 | 来源 | 说明 |
|------|------|------|
| ServiceName | UseAgentSerilog 参数 | 固定为 `Ruoyu.Study.DocLibrary` |
| ServiceVersion | UseAgentSerilog 参数 | 默认 `1.0.0` |
| InstanceId | Environment.MachineName | 实例标识 |
| MachineName | Enrichers.Environment | 主机名 |
| ThreadId | Enrichers.Thread | 线程 ID |

### Loki 地址注入

Loki 地址统一通过 `Loki:Uri` 配置键进入 `Serilog:WriteTo:1:Args:uri`：

| 来源 | 示例值 | 说明 |
|------|--------|------|
| `Loki:Uri` 配置键 | http://ruoyu-loki:3100 | 推荐由 Consul `config/ruoyu/shared.json` 提供 |
| `Loki:Uri`（fallback） | http://localhost:3100 | appsettings.json 中的兜底地址 |

> **容错机制**：如果 `Loki:Uri` 未设置，Loki Sink 使用 appsettings.json 中 `Serilog:WriteTo:1:Args:uri` 的 fallback 地址。Loki 不可达时 Sink 异步重试，不影响服务启动。`start.sh` 不传 `LOKI_URI` 环境变量，Loki 地址完全由 Consul 提供。

### 启动诊断

服务启动时会输出 Consul 拉取过程和最终生效配置摘要，包括 `LokiUri` 字段，便于排查 Loki 地址是否正确从 Consul 加载。

## 数据库配置

代码使用 `UseNpgsql`。本地开发请运行 PostgreSQL。

数据库连接字符串在运行时由以下两部分拼合得到：

- 共享 Consul 配置：`PostgreSql:Host/Port/Username/Password`
- 服务私有配置：`Database:Name=ruoyu_study_doclibrary`

启动后若配置正常，诊断日志应能看到最终生效的 PostgreSQL 主机与数据库名。

## 启动命令

```bash
dotnet run --project src/Host
```

Docker 部署时，`start.sh` 还会补：

```bash
--add-host=host.docker.internal:host-gateway
```

用于容器在 Linux Docker 环境下访问宿主机上的 Consul。

服务启动时自动执行：
1. `DatabaseInitializer.InitializeAsync` — 建表 + 列迁移（SQL-based，无 EF Core Migration）
2. `OpenSearchIndexService.EnsureIndexAsync` — 创建搜索索引（best-effort）
3. `IDocumentAnalysisService.InitializeAsync` — LLM 初始化（如配置了 ApiKey）

## 数据库备份与恢复

```bash
# 备份
docker exec ruoyu-postgres pg_dump -U postgres ruoyu_study_doclibrary | gzip > backup_doclibrary_$(date +%Y%m%d_%H%M%S).sql.gz

# 恢复
gunzip -c backup_doclibrary_20240101_020000.sql.gz | docker exec -i ruoyu-postgres psql -U postgres -d ruoyu_study_doclibrary
```
