# 04-TASKS — SegmentRefinement 任务列表

## 任务依赖图

```
TASK-100 (DB Schema)
  ├── TASK-101 (Entity + DDL)
  └── TASK-102 (Domain Models)
        │
TASK-200 (Backend API)
  ├── TASK-201 (Get Segments API)
  ├── TASK-202 (Refine API)
  └── TASK-203 (LLM Refinement)
        │
TASK-300 (Frontend)
  ├── TASK-301 (API Client)
  ├── TASK-302 (Segment View)
  └── TASK-303 (Router + Menu)
        │
TASK-400 (Tests)
  ├── TASK-401 (Backend Unit Tests)
  └── TASK-402 (Frontend Tests)
```

## TASK-100: 数据库层

### TASK-101: Entity + DDL 变更

- **文件**: `Database/Entities/DocumentEntity.cs`, `Database/DatabaseInitializer.cs`
- **改动**:
  - `DocumentEntity` 新增 `LlmProfileJson` 属性
  - `DatabaseInitializer` 新增 `document_segment_backups` 表 DDL
  - `DatabaseInitializer` 的 `documents` DDL 新增 `llm_profile_json` 列
- **依赖**: 无

### TASK-102: Domain Models

- **文件**: `Domain/Models/SegmentRefinementModels.cs` (新增)
- **改动**:
  - `SegmentCorrection` record
  - `DocumentSegmentsDto` record
  - `SegmentDto` record
  - `RefinementResult` record
  - `SegmentBackupEntity` entity
- **依赖**: TASK-101

## TASK-200: 后端 API

### TASK-201: Get Segments API

- **文件**: `Service/DocumentAdminEndpoints.cs`, `Domain/Services/IDocumentDomainService.cs`
- **改动**:
  - 新增 `GET /admin/documents/{id}/segments` 端点
  - `IDocumentDomainService.GetSegmentsAsync()`
  - `DocumentDomainService` 实现：查询 segments + pages + llm_profile_json
- **依赖**: TASK-102

### TASK-202: Refine API

- **文件**: `Service/DocumentAdminEndpoints.cs`, `Domain/Services/IDocumentDomainService.cs`
- **改动**:
  - 新增 `POST /admin/documents/{id}/refine` 端点
  - `IDocumentDomainService.RefineSegmentsAsync()`
  - 备份 → LLM refinement → 重建数据 → 重建索引
- **依赖**: TASK-201, TASK-203

### TASK-203: LLM Refinement Service

- **文件**: `Domain/Repositories/ILlmSegmentationService.cs`, `Service/LlmSegmentationService.cs`
- **改动**:
  - `RefineProfileAsync()` — 基于修正重新分析文档 profile
  - `RefineSegmentTextAsync()` — 基于修正重新分段
  - few-shot prompt 构造逻辑
- **依赖**: TASK-102

## TASK-300: 前端（DocLibrary 自带管理前端）

### TASK-301: API Client

- **文件**: `frontend/src/services/docApi.ts`
- **改动**:
  - 新增 `DocumentSegmentsData`、`SegmentDto`、`CorrectionDto`、`RefinementResult` 类型
  - `DocApiClient` 新增 `getDocumentSegments()` 和 `refineDocumentSegments()` 方法
- **依赖**: TASK-201, TASK-202

### TASK-302: Segment 管理对话框

- **文件**: `frontend/src/App.vue`
- **改动**:
  - 文档列表操作列新增"分段管理"按钮
  - 新增分段管理对话框（LLM 分析结果 + Segment 列表 + 合并/拆分/改类型）
  - 新增拆分对话框
- **依赖**: TASK-301

### TASK-303: 样式

- **文件**: `frontend/src/style.css`
- **改动**:
  - 新增 `.dialog-wide`、`.profile-section`、`.segment-*`、`.btn-success` 等样式
- **依赖**: TASK-302

## TASK-400: 测试

### TASK-401: Backend Unit Tests

- **文件**: `src/Tests/Services/DocumentRefinementTests.cs` (新增)
- **改动**:
  - GetSegments 测试
  - Merge corrections 测试
  - Split corrections 测试
  - Retype corrections 测试
  - LLM refinement prompt 构造测试
  - 备份/回滚测试
- **依赖**: TASK-200

### TASK-402: Frontend Tests

- 手动测试为主（当前项目无前端测试框架）
- **依赖**: TASK-300
