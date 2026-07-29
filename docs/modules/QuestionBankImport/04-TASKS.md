# 04-TASKS — QuestionBank 只读解析数据接口任务

| 编号 | 任务 | 验收结果 |
|------|------|----------|
| QBI-01 | 将三条查询路由迁移到 `/internal/question-bank/*` | `/admin` 下无 QuestionBank 内部路由 |
| QBI-02 | 增加服务密钥认证策略 | 缺失/错误密钥 401，正确密钥允许查询 |
| QBI-03 | 移除 import-status 端点与 DTO | 无内部写接口 |
| QBI-04 | 移除导入状态领域、仓储和 EF 运行时组件 | DocLibrary 不再读写导入状态 |
| QBI-05 | 从列表查询移除 LEFT JOIN、includeImported 和状态字段 | 查询只依赖 parse/file |
| QBI-06 | 保留现有 parse/block/image 查询行为 | 分页、过滤、图片读取不回归 |
| QBI-07 | 更新测试和部署配置 | 覆盖只读与服务密钥边界 |
