# 本地搭建

如何在本地搭建并运行 `doctheca`。

## 前置条件

| 依赖 | 是否必需 | 说明 |
|------|----------|------|
| .NET 8 SDK | 是 | 项目目标框架为 `net8.0` |
| PostgreSQL | 是 | 默认连接字符串指向 `localhost:5432`（`doctheca`）|
| OpenSearch | 可选 | 全文搜索索引；默认地址为 `http://localhost:9200`；推荐 Docker 镜像版本 `2.19.5`（与 `OpenSearch.Net 1.8.0` 客户端兼容） |
| MinIO / SeaweedFS | 可选 | S3 兼容的对象存储；本地 fallback 的 `Oss:InternalEndpoint` 为 `localhost:8333`；可通过 `USE_LOCAL_OSS=1` 切换为本地文件系统存储 |

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `USE_LOCAL_OSS` | （未设置） | 设为 `1` 可使用本地文件系统存储代替 S3，文件保存至 `OSS_LOCAL_PATH` |
| `OSS_LOCAL_PATH` | `data/oss` | 本地文件存储目录（仅在 `USE_LOCAL_OSS=1` 时使用） |

无需其他环境变量。直接本地运行时，其余配置来自 `appsettings.json`；接入 Consul 后，
`Oss:InternalEndpoint`、`Oss:InternalSecure` 和 `Oss:PublicBaseUrl` 等共享配置由
`config/ruoyu/shared.json` 覆盖。

## 数据库配置

服务使用 **PostgreSQL**（`UseNpgsql`，连接字符串来自 `ConnectionStrings:Default`）。默认的 `appsettings.json` 包含：

```
Host=localhost;Port=5432;Database=doctheca;Username=phil
```

数据库和表会在启动时通过 `DatabaseInitializer.InitializeAsync` 自动创建（`CREATE TABLE IF NOT EXISTS`）。

## 运行服务

在 `` 目录下执行：

```bash
dotnet run --project src/Host
```

### 端口配置

| 协议 | 默认端口 | 配置键 | 说明 |
|------|----------|--------|------|
| HTTP | 5012 | `Endpoints:Http` | 管理 API + 搜索 API + 健康检查 |

端口可在 `appsettings.json` 的 `Endpoints` 节中覆盖。

## 最小本地搭建（仅 OSS 用本地存储）

在不依赖外部对象存储的情况下运行（PostgreSQL 仍必需）：

1. 设置环境变量 `USE_LOCAL_OSS=1`（可选指定 `OSS_LOCAL_PATH`，默认 `data/oss`）。
2. 运行 `dotnet run --project src/Host`。

你将获得：
- PostgreSQL 数据库（自动创建表）
- 本地文件系统对象存储（文件保存至 `data/oss/`）
- 通过 HTTP 管理 API 进行文档上传、列表、删除和元数据更新
- 后台解析工作器（解析功能完整；OpenSearch 不可用时搜索返回空结果）

## 需要外部服务的功能

| 功能 | 服务 | 配置节 | 缺少时的行为 |
|------|------|--------|-------------|
| 全文搜索 | OpenSearch | `OpenSearch` | 搜索返回空结果（`SearchDomainService` 返回空 + LogWarning）；索引初始化时记录警告 |
