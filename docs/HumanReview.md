# HumanReview — ruoyu.docretrieval

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

| ID | 问题 | 位置 | 状态 |
|----|------|------|------|
| HR-01 | DatabaseSearchAsync 全表扫描 + 内存分页 | SearchDomainService.cs | 待修复 |

## P1 — 应尽快修复

| ID | 问题 | 位置 | 状态 |
|----|------|------|------|
| HR-02 | DocumentAdminEndpoints 无认证 | DocumentAdminEndpoints.cs | 待修复 |
| HR-03 | UpdateMetadata 端点手动读取 body 无大小限制 | DocumentAdminEndpoints.cs | 待修复 |
| HR-04 | 单文件包含多个类型 | 多处 | 待修复 |
| HR-05 | SearchDomainService 使用 IServiceProvider.GetService 而非 DI | SearchDomainService.cs | 待修复 |
