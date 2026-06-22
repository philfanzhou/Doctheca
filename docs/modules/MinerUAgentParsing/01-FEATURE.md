# 01-FEATURE — MinerU Document Parsing 功能概述

## 功能名称

**MinerU Document Parsing** — MinerU 文档解析集成（Precision Extract API）

## 功能概述

MinerU Document Parsing 模块集成 MinerU Precision Extract API，将文档（PDF/DOCX/PPTX/图片）解析为 Markdown 格式（含图片）。使用 MinerU 在线 API（需 Token，每日 1000 页免费额度），输出 ZIP 包含 Markdown + 图片目录，图片上传到自有 OSS 后替换为可访问 URL。

整个流程：
1. 用户在管理页面上传文档
2. 后端将文件上传到自有 OSS，获取 presigned URL
3. 用 presigned URL 调用 MinerU Precision Extract API 提交解析任务
4. 前端轮询解析状态，完成后后端下载 ZIP 包
5. 后端解压 ZIP，将图片上传到自有 OSS，替换 Markdown 中的相对路径为 OSS presigned URL
6. 前端渲染 Markdown 预览（含图片），支持查看源码和渲染效果

## 单一用户故事

> **作为** DocRetrieval 管理员，
> **我希望** 在管理页面上传文档并调用 MinerU Precision API 解析为 Markdown（含图片），
> **以便** 获得包含图片的完整文档结构化输出，无需自建解析服务。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | 管理页面 "MinerU 解析" Tab，支持上传文件（PDF/DOCX/PPTX/图片，≤200MB） | 手动测试 |
| AC-2 | 上传文件后调用 Precision API 提交解析任务，返回 task_id | API 测试 |
| AC-3 | 前端每 3 秒轮询解析状态，显示进度（pending/processing/done/failed） | 手动测试 |
| AC-4 | 解析完成后后端下载 ZIP 包，解压并处理图片 | API 测试 |
| AC-5 | 图片上传到自有 OSS，Markdown 中相对路径替换为 OSS presigned URL | API 测试 |
| AC-6 | 前端渲染 Markdown 预览（含图片），支持查看源码和渲染效果 | 手动测试 |
| AC-7 | 支持下载 Markdown 文件到本地 | 手动测试 |
| AC-8 | 解析选项可配置：模型版本（pipeline/vlm）、OCR 开关、公式识别、表格识别 | API 测试 |
| AC-9 | 文件超过 200MB 时前端提示限制 | 手动测试 |
| AC-10 | API 调用失败时显示错误信息 | 模拟网络异常 |
| AC-11 | Token 配置在 appsettings.json，未配置时提示 | 手动测试 |

## 范围内

- MinerU Precision API 客户端服务（提交、轮询、下载 ZIP、解压、图片处理）
- Admin API 端点（提交解析、查询状态、获取结果）
- 管理页面 MinerU 解析 Tab（上传、进度、预览、下载）
- 解析选项配置（模型版本/OCR/公式/表格）
- 图片上传到自有 OSS 并替换 Markdown 路径

## 范围外

- 解析结果持久化到数据库（后续优化）
- 解析结果接入搜索索引（后续优化）
- 替代现有 DocumentParserService（后续优化）
- Batch 批量文件解析（后续优化）
- Agent Lightweight API 保留为降级方案（无需 Token，但无图片）

## API 选型对比

| 维度 | Agent Lightweight API | Precision Extract API（当前选用） |
|---|---|---|
| Token | 不需要 | 需要（注册即得，每日 1000 页免费） |
| 端点 | `/api/v1/agent/parse/file` | `/api/v4/extract/task` |
| 文件限制 | ≤10MB / ≤20页 | ≤200MB / ≤200页 |
| 输出 | 仅 Markdown CDN 链接 | ZIP 包（Markdown + 图片 + JSON） |
| 图片支持 | `<!-- image-->` 占位符，无实际图片 | `![](images/xxx.jpg)` + images/ 目录 |
| 模型版本 | 固定 pipeline 轻量模型 | pipeline / vlm（推荐） |

## Precision Extract API 技术要点

| 项 | 值 |
|---|---|
| 提交端点 | `POST https://mineru.net/api/v4/extract/task` |
| 轮询端点 | `GET https://mineru.net/api/v4/extract/task/{task_id}` |
| 认证 | Bearer Token（appsettings.json 配置） |
| 文件限制 | ≤200MB，≤200 页，单文件 |
| 输出 | ZIP 包（`full.md` + `images/` + `layout.json`） |

### 提交流程（已实测验证）

1. 后端将用户上传的文件保存到自有 OSS，获取 presigned URL
2. POST JSON body `{url, model_version, data_id, is_ocr, enable_formula, enable_table}` + Bearer Token
3. 返回 `{task_id}`，用于后续轮询
4. 轮询 `GET /api/v4/extract/task/{task_id}` 直到 `state=done`
5. 从 `data.full_zip_url` 下载 ZIP 包
6. 解压 ZIP：`full.md`（Markdown）、`images/`（图片目录）
7. 将 `images/` 下的图片上传到自有 OSS
8. 替换 `full.md` 中的 `![](images/xxx.jpg)` 为 `![](oss_presigned_url)`
9. 返回处理后的 Markdown 给前端

### 提交请求体

```json
{
  "url": "https://our-oss.example.com/documents/xxx.pdf?presigned-params",
  "model_version": "vlm",
  "data_id": "optional-custom-id",
  "is_ocr": false,
  "enable_formula": true,
  "enable_table": true
}
```

### 轮询响应

```json
{
  "code": 0,
  "msg": "ok",
  "data": {
    "task_id": "xxx",
    "state": "done",
    "full_zip_url": "https://cdn-mineru.openxlab.org.cn/pdf/.../xxx.zip",
    "err_msg": null
  }
}
```

### 响应状态

| state | 含义 |
|-------|------|
| pending | 排队中 |
| processing | 解析中 |
| done | 完成（full_zip_url 可用） |
| failed | 失败（err_msg 可用） |

## 配置项

```json
{
  "MinerU": {
    "ApiToken": "eyJ0eXAi...",
    "BaseUrl": "https://mineru.net"
  }
}
```

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| ApiToken | Precision API Bearer Token | 空（未配置则功能不可用） |
| BaseUrl | MinerU API 基础 URL | https://mineru.net |

注：模型版本固定为 `vlm`（效果最好），无需配置。

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| MinerU Precision 客户端 | `src/Service/MinerUPrecisionClient.cs` |
| MinerU Agent 客户端（降级） | `src/Service/MinerUAgentClient.cs` |
| Admin 端点 | `src/Service/DocumentAdminEndpoints.cs` |
| 前端页面 | `frontend/src/App.vue` |
