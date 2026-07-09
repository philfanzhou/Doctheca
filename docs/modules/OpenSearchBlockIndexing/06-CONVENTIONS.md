# OpenSearchBlockIndexing — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 内部可测试方法 | `internal static` | `BuildIndexBody()` |
| OpenSearch _id | `block_{blockId}` | `block_abc123...` |
| 索引名 | 配置驱动 | `doclibrary-segments` |

## 日志约定

- 索引成功：`LogInformation("Parse {ParseId} indexed {BlockCount} blocks to OpenSearch (FileId={FileId})")`
- 索引失败：`LogWarning("Parse {ParseId} indexing failed, status code: {StatusCode}")`
- 删除成功：`LogInformation("Parse {ParseId} search index deleted")`
- 删除失败：`LogWarning("Failed to delete parse {ParseId} search index, status code: {StatusCode}")`

## Bulk 索引约定

- 每 block 生成 2 行 JSON（index 指令 + 文档）
- 行间用 `\n` 分隔，末尾加 `\n`
- text_content 为 null/empty 的 block 跳过（不索引）
- _id 使用 `block_{blockId}` 格式，保证幂等

## 错误处理约定

所有 OpenSearch 操作均为 best-effort：
- 调用方捕获异常后记录 `LogWarning`
- 不抛异常给上层（Worker / 端点）
- 索引失败不影响解析流程
- 删除失败不影响文件/解析删除流程
