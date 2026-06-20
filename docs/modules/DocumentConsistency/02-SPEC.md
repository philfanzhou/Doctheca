# DocumentConsistency — 详细需求规格 (SPEC)

## 功能概述

**概述**：OSS 文件与数据库记录的一致性扫描和清理。用于发现并修复两类数据不一致问题：
1. 数据库有文档记录但 OSS 文件不存在（brokenDocuments）
2. OSS 有文件但数据库无对应记录（orphanOssFiles）

**产生原因**：
- 上传时 OSS 先于 DB 写入，DB 写入失败导致孤儿文件
- 外部手动删除 OSS 文件导致文档记录悬空
- 删除文档时 OSS 删除失败（DB 已删，OSS 残留）

## 功能要求清单

- [x] REQ-CONSISTENCY-01 扫描端点：`GET /admin/documents/scan-consistency`
  - 查询所有 `documents` 记录的 `file_path`（去重）
  - 逐个调用 `IOssService.ObjectExistsAsync` 检查 OSS 文件是否存在
  - 调用 `IOssService.ListObjectsAsync("docretrieval/")` 列出所有 OSS 文件
  - 反向比对：OSS 文件路径不在任何 document 的 `file_path` 中 = 孤儿文件
  - 返回：`{ orphanOssFiles: string[], brokenDocuments: { id, title, filePath, status }[] }`

- [x] REQ-CONSISTENCY-02 强制删除端点：`DELETE /admin/documents/{id}/force`
  - 获取文档记录（不存在返回 404）
  - 删除 DB 记录（级联删除 pages/segments/occurrences/jobs）
  - 尝试删除 OSS 文件（失败不阻塞，仅记日志）
  - 同步删除搜索索引
  - 返回删除结果

- [x] REQ-CONSISTENCY-03 扫描效率：DB 侧按 `file_path` 去重后检查 OSS，不逐条记录检查
- [x] REQ-CONSISTENCY-04 扫描效率：OSS 侧检查是否有任意 document 关联即可，不逐文件查 DB

## 验收场景

```gherkin
Scenario: 扫描发现孤儿 OSS 文件
  Given OSS 中存在文件 "docretrieval/orphan.pdf" 但 DB 无对应记录
  When 调用 GET /admin/documents/scan-consistency
  Then 返回 orphanOssFiles 包含 "docretrieval/orphan.pdf"
  And brokenDocuments 为空

Scenario: 扫描发现悬空文档记录
  Given DB 中存在文档记录且 file_path 指向已删除的 OSS 文件
  When 调用 GET /admin/documents/scan-consistency
  Then 返回 brokenDocuments 包含该文档
  And orphanOssFiles 为空

Scenario: 强制删除悬空文档
  Given 文档 ID 存在但 OSS 文件已丢失
  When 调用 DELETE /admin/documents/{id}/force
  Then DB 记录被级联删除
  And 返回 success

Scenario: 强制删除不存在的文档
  Given 文档 ID 不存在
  When 调用 DELETE /admin/documents/{id}/force
  Then 返回 404
```
