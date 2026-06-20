# DocumentConsistency — 测试计划

## 单元测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-01 | 所有文件存在 | DB 有 1 条记录，OSS 文件存在 | brokenDocuments 为空，orphanOssFiles 为空 |
| T-02 | OSS 文件缺失 | DB 有 1 条记录，OSS 文件不存在 | brokenDocuments 包含该文档 |
| T-03 | 孤儿 OSS 文件 | DB 无记录，OSS 有 1 个文件 | orphanOssFiles 包含该文件 |
| T-04 | 混合场景 | 部分文件缺失 + 部分孤儿 | 两个列表各有对应条目 |
| T-05 | 重复 file_path | 两条文档记录指向同一 OSS 文件 | OSS 检查只执行一次 |
| T-06 | created_by 保留 | 上传时设置 CreatedBy | CreateDocumentAsync 保留 CreatedBy |
| T-07 | created_by 为空 | 上传时未设置 CreatedBy | CreatedBy 为 null |
