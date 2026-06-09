# 文档管理接口

## 文档列表

- 方法：`GET /admin/documents`
- 支持参数：
  - `keyword`
  - `subject`
  - `grade`
  - `year`
  - `status`
  - `page`
  - `pageSize`

返回内容：

- 文档分页列表
- 总数
- 当前页
- 每页大小

## 修改元数据

- 方法：`PUT /admin/documents/{title}/metadata`
- 可修改字段：
  - `subject`
  - `grade`
  - `year`
  - `tags`

失败场景：

- `DOCRETRIEVAL_DOCUMENT_NOT_FOUND`
- `DOCRETRIEVAL_DOCUMENT_NOT_READY`
- `DOCRETRIEVAL_SUBJECT_INVALID`
- `DOCRETRIEVAL_GRADE_INVALID`
- `DOCRETRIEVAL_IMMUTABLE_FIELD`

## 删除文档

- 方法：`DELETE /admin/documents/{title}`
- 语义：硬删除，幂等

## 状态查看

- 方法：`GET /admin/documents/{id}/status`
- 用途：从管理视角查看一份文档的导入过程和任务历史
