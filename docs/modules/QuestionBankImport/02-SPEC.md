# 02-SPEC — QuestionBank 只读解析数据接口规格

## 鉴权

所有端点要求请求头：

```http
X-DocLibrary-Service-Key: <configured service key>
```

服务端从 `InternalAuth:QuestionBankKey` 读取期望值并使用固定时间比较。配置缺失、请求头缺失或值不匹配均返回 401。管理员 JWT 不替代服务密钥。

## GET `/internal/question-bank/document-parses`

查询参数：

| 参数 | 默认值 | 约束 |
|------|--------|------|
| `page` | 1 | 小于 1 时修正为 1 |
| `pageSize` | 20 | 小于 1 时修正为 20，最大 100 |
| `search` | 空 | 可选，按文件名模糊查询 |

只返回 `document_parses.status=parsed`，按 `parsed_at DESC`、`id DESC` 排序：

```json
{
  "success": true,
  "data": [
    {
      "parseId": "uuid",
      "fileId": "uuid",
      "fileName": "paper.pdf",
      "modelVersion": "vlm",
      "parsedAt": "2026-07-29T10:00:00.000Z"
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 20,
  "totalPages": 1
}
```

接口不接受 `includeImported`，也不查询导入状态。

## GET `/internal/question-bank/document-parses/{parseId}/blocks`

查询参数：

| 参数 | 默认值 | 约束 |
|------|--------|------|
| `page` | 1 | 小于 1 时修正为 1 |
| `pageSize` | 50 | 小于 1 时修正为 50，最大 200 |
| `pageId` | 空 | 0 是有效值 |
| `blockType` | 空 | 按现有 block_type 精确过滤 |

- parse 不存在：404 + `DOCLIBRARY_PARSE_NOT_FOUND`
- parse 未完成：422 + `DOCLIBRARY_PARSE_NOT_PARSED`
- `blockData` 是解析后的 JSON；非法 JSON 返回 null 并记录不含原始敏感数据的 Warning
- 有关联图片的 block 返回图片元数据和短期 presigned URL

## GET `/internal/question-bank/images/{imageId}`

- 图片存在：200，按数据库 `content_type` 返回二进制流
- 图片不存在：404 + `DOCLIBRARY_IMAGE_NOT_FOUND`
- OSS 下载失败：500 + `DOCLIBRARY_OSS_DOWNLOAD_FAILED`

## 幂等责任

DocLibrary 查询接口不承诺 exactly-once delivery。同一 parse 可以被多次查询。QuestionBank 必须：

1. 把 DocLibrary `parseId` 保存为来源键。
2. 对来源键建立唯一约束。
3. 在一个 QuestionBank 数据库事务中完成题目写入和来源记录。
4. 重试时读取自身来源记录并跳过已成功处理的 parse。

该设计避免跨 DocLibrary/QuestionBank 数据库的非原子“双写状态”。
