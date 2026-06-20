# DocumentConsistency — 测试计划

## 单元测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-01 | 所有文件存在 | DB 有 1 条记录，OSS 文件存在 | brokenDocuments 为空，orphanOssFiles 为空 |
| T-02 | OSS 文件缺失 | DB 有 1 条记录，OSS 文件不存在 | brokenDocuments 包含该文档 |
| T-03 | 孤儿 OSS 文件 | DB 无记录，OSS 有 1 个文件 | orphanOssFiles 包含该文件 |
| T-04 | 混合场景 | 部分文件缺失 + 部分孤儿 | 两个列表各有对应条目 |
| T-05 | 重复 file_path 去重 | 两条文档记录指向同一 OSS 文件 | OSS 检查只执行一次（Times.Once） |
| T-06 | 强制删除正常流程 | 文档存在，OSS 和搜索索引均正常 | DB 级联删除 + OSS 删除 + 索引删除成功 |
| T-07 | 强制删除 OSS 失败 | 文档存在，OSS 删除抛异常 | DB 删除成功，整体仍返回 success |
| T-08 | 强制删除文档不存在 | 文档 ID 不存在 | 返回 404 |

> created_by 相关测试（T-06/T-07）见 `DocumentDomainServiceTests.cs`。
