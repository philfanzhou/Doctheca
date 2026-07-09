# Deployment — 部署与运维

## 服务端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 5012 | HTTP | Web 管理界面 + 搜索 API + 静态文件 + Health Check |

端口可在 `appsettings.json` 的 `Endpoints:Http` 中覆盖。

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LOKI_URI` | （未设置） | Grafana Loki 地址，覆盖 appsettings.json 中的 fallback |
| `USE_LOCAL_OSS` | （未设置） | 设为 `1` 使用本地文件系统存储代替 S3 |
| `OSS_LOCAL_PATH` | `data/oss` | 本地文件存储目录（仅在 `USE_LOCAL_OSS=1` 时使用） |

## 下游依赖

| 依赖 | 端口 | 用途 |
|------|------|------|
| PostgreSQL | 5432 | 主数据库（`ruoyu_study_doclibrary`） |
| OpenSearch | 9200 | 全文检索索引 |
| MinIO / SeaweedFS | 8333 | 对象存储（S3 兼容） |

> **不依赖 QuantumZhou.Identity**：内网管理后台，访问控制由部署层网络隔离实现。

## 数据库配置

```json
{
  "ConnectionStrings": {
    "Default": "Host=ruoyu-postgres;Port=5432;Database=ruoyu_study_doclibrary;Username=postgres;Password=postgres"
  }
}
```

本地开发可使用 SQLite（连接字符串不包含 `Host=` / `Server=` 时自动切换）。

## 启动命令

```bash
dotnet run --project src/Host
```

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
