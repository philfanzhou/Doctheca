# 本地搭建

如何在本地搭建并运行 `ruoyu.docretrieval`。

## 前置条件

| 依赖 | 是否必需 | 说明 |
|------|----------|------|
| .NET 8 SDK | 是 | 项目目标框架为 `net8.0` |
| PostgreSQL | 可选 | `appsettings.json` 中的默认连接字符串指向 `localhost:5432` |
| SQLite | 可选 | 自动检测的回退方案；无需安装（EF Core SQLite 提供程序已内置） |
| OpenSearch | 可选 | 全文搜索索引；默认地址为 `http://localhost:9200`；推荐 Docker 镜像版本 `2.19.5`（与 `OpenSearch.Net 1.8.0` 客户端兼容） |
| MinIO / SeaweedFS | 可选 | S3 兼容的对象存储；默认地址为 `localhost:8333` |

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `USE_LOCAL_OSS` | （未设置） | 设为 `1` 可使用本地文件系统存储代替 S3，文件保存至 `OSS_LOCAL_PATH` |
| `OSS_LOCAL_PATH` | `data/oss` | 本地文件存储目录（仅在 `USE_LOCAL_OSS=1` 时使用） |

无需其他环境变量，其余配置均来自 `appsettings.json`。

## 数据库：SQLite 自动检测

服务根据 `appsettings.json` 中的连接字符串选择数据库提供程序：

- 如果连接字符串包含 `Host=` 或 `Server=`（不区分大小写）→ **PostgreSQL**（`UseNpgsql`）
- 否则 → **SQLite**（`UseSqlite`）

默认的 `appsettings.json` 包含 PostgreSQL 连接字符串：

```
Host=localhost;Port=5432;Database=ruoyu_study_docretrieval;Username=phil
```

若要改用 SQLite，请将 `ConnectionStrings:Default` 改为 SQLite 风格的字符串，例如：

```json
"ConnectionStrings": {
  "Default": "Data Source=data/sqlite/ruoyu_study_docretrieval.db"
}
```

如果连接字符串为空或 null，SQLite 默认使用 `Data Source=data/sqlite/ruoyu_study_docretrieval.db`。

数据库和表会在启动时通过 `DatabaseInitializer.InitializeAsync` 自动创建。

## 运行服务

在 `src/services/ruoyu.docretrieval/` 目录下执行：

```bash
dotnet run --project src/Host
```

### 端口配置

| 协议 | 默认端口 | 配置键 | 说明 |
|------|----------|--------|------|
| gRPC | 5011 | `Endpoints:Grpc` | 仅 HTTP/2 |
| HTTP | 5012 | `Endpoints:Http` | 仅 HTTP/1（管理 API + 健康检查） |

端口可在 `appsettings.json` 的 `Endpoints` 节中覆盖。

## 最小本地搭建（无需外部服务）

在没有任何外部服务的情况下实现基本的上传/列表/删除功能：

1. 将连接字符串设为 SQLite 值（移除 `Host=` / `Server=`）。
2. 设置环境变量 `USE_LOCAL_OSS=1`。
3. 运行 `dotnet run --project src/Host`。

你将获得：
- SQLite 数据库（自动创建）
- 本地文件系统对象存储（文件保存至 `data/oss/`）
- 通过 HTTP 管理 API 进行文档上传、列表、删除和元数据更新
- 后台摄取工作器（仅解析；搜索索引将优雅降级）

## 需要外部服务的功能

| 功能 | 服务 | 配置节 | 缺少时的行为 |
|------|------|--------|-------------|
| 全文搜索 | OpenSearch | `OpenSearch` | 回退到数据库 LIKE 搜索；索引初始化时记录警告 |
