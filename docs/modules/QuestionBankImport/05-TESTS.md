# 05-TESTS — QuestionBank 只读解析数据接口测试

## 服务测试

| 编号 | 场景 | 预期 |
|------|------|------|
| QBI-UT-01 | 查询 parsed parse | 仅返回 parsed，按时间倒序 |
| QBI-UT-02 | 搜索文件名 | 只返回匹配记录 |
| QBI-UT-03 | 非法分页参数 | 修正到规定范围 |
| QBI-UT-04 | 查询 blocks | 顺序、分页和 JSON 解析正确 |
| QBI-UT-05 | 按 pageId=0 过滤 | 0 不被当作空值 |
| QBI-UT-06 | 按 blockType 过滤 | 只返回匹配类型 |
| QBI-UT-07 | parse 不存在或未完成 | 分别映射 404/422 |
| QBI-UT-08 | 图片存在/不存在/OSS 失败 | 分别映射 200/404/500 |

## 授权测试

| 编号 | 场景 | 预期 |
|------|------|------|
| QBI-AUTH-01 | 无服务密钥 | 401 |
| QBI-AUTH-02 | 错误服务密钥 | 401 |
| QBI-AUTH-03 | 正确服务密钥 | 请求进入端点 |
| QBI-AUTH-04 | 仅管理员 Cookie/JWT | 401 |
| QBI-AUTH-05 | 旧 `/admin/document-parses/importable` 等路径 | 不再映射 |
| QBI-AUTH-06 | 任意 import-status POST | 不再映射 |

## 数据边界检查

- 测试项目不再引用 `DocumentParseImport*` 类型。
- `GetImportableListAsync` 不接受 `includeImported`。
- 响应不包含 `importStatus`、`importedAt`、`importedQuestionIds`。
