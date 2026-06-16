# 部署与运�?

## 构建与部�?

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：见项目 scripts/ 目录

## 配置�?

### 数据库连�?

```json
{
  "ConnectionStrings": {
    "Default": "Host=ruoyu-postgres;Port=5432;Database=ruoyu_study_docretrieval;Username=postgres;Password=postgres"
  }
}
```

### 服务端口

| 端口 | 协议 | 用�?|
|------|------|------|
| 5011 | gRPC | 文档检索服�?|
| 5012 | HTTP | Web 管理界面 |

### 下游依赖

| 依赖 | 端口 | 用�?|
|------|------|------|
| SeaweedFS | 8333 | 对象存储 |
| OpenSearch | 9200 | 全文检�?|
| Qdrant | 6333 | 向量检�?|

## 数据库备份与恢复

```bash
# 备份
docker exec ruoyu-postgres pg_dump -U postgres ruoyu_study_docretrieval | gzip > backup_docretrieval_$(date +%Y%m%d_%H%M%S).sql.gz

# 恢复
gunzip -c backup_docretrieval_20240101_020000.sql.gz | docker exec -i ruoyu-postgres psql -U postgres -d ruoyu_study_docretrieval
```
