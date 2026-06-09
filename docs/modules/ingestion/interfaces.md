# 文档导入接口

## 管理接口

### 上传文档

- 方法：`POST /admin/documents/upload`
- Content-Type：`multipart/form-data`
- 必填字段：
  - `file`
  - `title`
  - `subject`
  - `grade`
  - `year`
- 可选字段：
  - `tags`

成功返回：

- `document_id`
- `title`
- `job_id`
- `status`

失败返回：

- `DOCRETRIEVAL_TITLE_ALREADY_EXISTS`
- `DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS`
- `DOCRETRIEVAL_FILE_ENCRYPTED`
- `DOCRETRIEVAL_FILE_FORMAT_UNSUPPORTED`
- `DOCRETRIEVAL_METADATA_REQUIRED`
- `DOCRETRIEVAL_SUBJECT_INVALID`
- `DOCRETRIEVAL_GRADE_INVALID`

### 查看导入状态

- 方法：`GET /admin/documents/{id}/status`
- 返回：
  - 文档基本信息
  - 当前任务状态
  - parser / OCR 版本
  - 错误信息
  - 开始 / 结束时间

## 接口边界

- 上传与状态查看只面向 Web 管理界面
- 业务系统不调用本模块接口
