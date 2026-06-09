# 文档上传与导入任务创建 (DocumentUpload)

## 功能名称和一句话概括

文档上传与导入任务创建 — 管理员通过 Admin 界面上传文档（PDF/Word/PPT），系统自动计算文件 SHA-256 哈希、上传到 OSS、创建文档记录和导入任务，基于标题唯一性与文件哈希去重实现幂等保护。

## 核心用户故事

**管理员上传文档**：作为管理员，我希望通过 Admin 界面上传一份教学文档（PDF/Word/PPT），系统自动完成文件校验（格式、大小、加密检测）、哈希计算、OSS 存储、文档记录创建与导入任务排队，让我无需手动处理文件存储和任务调度，且在重复上传相同标题或相同文件时得到明确拒绝提示。

## 关键验收条件摘要

- AC-1：必须提供 `title`/`subject`/`grade`/`year` 四项元数据，缺少任何一项返回 400（`DOCRETREIVAL_METADATA_REQUIRED`）。
- AC-2：文件大小 ≤ 200MB，仅支持 PDF/Word/PPT 格式，超限或格式不符返回 400（`DOCRETREIVAL_FILE_FORMAT_UNSUPPORTED` / `DOCRETREIVAL_FILE_REQUIRED`）。
- AC-3：加密 PDF 被拒绝，返回 400（`DOCRETREIVAL_FILE_ENCRYPTED`）。
- AC-4：相同标题已存在时返回 409（`DOCRETREIVAL_TITLE_ALREADY_EXISTS`）。
- AC-5：相同文件哈希且状态为 `ready` 的文档已存在时返回 409（`DOCRETREIVAL_FILE_HASH_ALREADY_EXISTS`）。
- AC-6：学科仅支持"英语"，年级必须为 K/G1~G12，不合法值返回 400（`DOCRETREIVAL_SUBJECT_INVALID` / `DOCRETREIVAL_GRADE_INVALID`）。
- AC-7：文档记录和导入任务在同一 `SaveChangesAsync` 中原子写入，保证数据一致性。
- AC-8：OSS 路径格式为 `docretrieval/{Guid}{ext}`，SHA-256 哈希计算后 `stream.Position=0` 再上传 OSS。

## 明确列出"范围外"（不做什么）

- 不处理用户认证与权限校验（由上层 API 网关负责）。
- 不实现 OSS 存储桶配置变更与生命周期管理（由 `IOssService` 实现处理）。
- 不处理文档的解析、分页、分段、向量化等导入逻辑（由 `IngestionWorker` 负责）。
- 不提供文档的查询、删除、元数据更新等管理功能（由其他端点负责）。
- 不实现前端 UI 组件与交互细节。
- 不实现审计日志、操作历史回放等非本功能所需能力。

## 文档索引

| 文档 | 说明 |
| --- | --- |
| [SPEC.md](./02-SPEC.md) | 详细需求与验收标准清单 |
| [DESIGN.md](./03-DESIGN.md) | 架构设计、接口签名与数据流 |
| [TASKS.md](./04-TASKS.md) | 开发/验证任务列表 |
| [TESTS.md](./05-TESTS.md) | 单元测试与集成测试计划 |
| [CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
