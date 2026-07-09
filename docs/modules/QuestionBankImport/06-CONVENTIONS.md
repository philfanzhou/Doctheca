# QuestionBankImport — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 端点类 | 静态类，PascalCase + Endpoints 后缀 | `QuestionBankImportEndpoints` |
| 端点方法 | 私有静态，PascalCase | `ListImportableParses` |
| Record（数据传输） | PascalCase | `ImportableParseItem`、`ParseBlockItem`、`ParseImageBlob` |
| 状态常量 | PascalCase | `ParseImportStatus.Imported`、`ParseImportStatus.Failed` |

## 日志约定

- 关键操作记录结构化日志
- 图片下载失败：`LogError`
- 列表查询：无额外日志（高频操作）

关键日志消息：
```
"Failed to download image {ImageId} from OSS path {ImagePath}"
"Block {BlockId} has image_id {ImageId} but image record not found (data inconsistency)"
```

## 错误处理约定

- `KeyNotFoundException` → 404（parse/image 不存在）
- `InvalidOperationException` → 422（状态不满足 / 重复导入）
- `ArgumentException` → 400（参数非法）
- OSS 异常向上抛 → 500

## blockData JSON 解析约定

- 使用 `JsonSerializer.Deserialize<JsonElement>` 反序列化
- 非法 JSON → 返回 null，记录 Warning 日志
- 空字符串 / "{}" → 正常返回空对象

## Presigned URL 约定

- 过期时间：3600 秒（1 小时）
- 并行生成：使用 `Task.WhenAll` 批量生成
