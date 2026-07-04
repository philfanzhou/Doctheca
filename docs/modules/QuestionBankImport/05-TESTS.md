# 05-TESTS — QuestionBankImport 测试文档

## 测试策略

- **测试框架**:xUnit + Moq + FluentAssertions(与项目现有测试栈一致)
- **DbContext**:使用 EF Core InMemory provider 构造 `DocLibraryDbContext`,避免依赖真实 PostgreSQL
- **依赖 Mock**:
  - `IDocumentParseImportRepository`:Mock 首次/已存在/更新场景
  - `IOssService`:Mock `DownloadAsync` / `GetPresignedUrlAsync` 成功与异常
  - `ILogger<QuestionBankImportService>`:Mock 验证日志输出
- **测试目标类**:`QuestionBankImportService`(Service 层),Endpoint 层的 HTTP 语义留到集成测试

## 单元测试

### GetImportableListAsync — 可导入列表

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-QBI-01 | 仅返回 status=parsed 的 parse | FR-01 / AC-FR-01 | 1 个 parsed + 1 个 pending parse | 列表仅含 parsed 记录,total=1 |
| UT-QBI-02 | search 模糊匹配 file_name | FR-02 / AC-FR-02 | 2 个 parsed,文件名含"期末"/"月考" | search=期末 仅返回含"期末"的记录 |
| UT-QBI-03 | includeImported=false 排除 imported | FR-03 / AC-FR-03 | A 无导入记录,B import_status=imported | 默认仅返回 A,B 被排除 |
| UT-QBI-04 | includeImported=true 包含 imported | FR-03 / AC-FR-03 | 同 UT-QBI-03 | 返回 A 和 B,B 的 importStatus="imported"、importedAt 不为 null |
| UT-QBI-05 | failed 状态不被排除 | FR-03 / AC-FR-03 | C import_status=failed | 默认返回 C,importStatus="failed" |
| UT-QBI-06 | 分页参数修正 | NFR-04 / AC-FR-01 | page=0、pageSize=0、pageSize=200 | page 修正为 1,pageSize 修正为 20 或 100 |
| UT-QBI-07 | 按 parsed_at DESC 排序 | AC-FR-01 | 3 个 parsed,时间递增 | 返回顺序为最新在前 |

### GetBlocksAsync — 结构化块

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-QBI-08 | parse 不存在抛 KeyNotFoundException | FR-04 / AC-FR-04 | parseId 不在 DocumentParses | 抛 KeyNotFoundException(Endpoint 转 404) |
| UT-QBI-09 | parse 状态非 parsed 抛 InvalidOperationException | FR-04 / AC-FR-04 | parse.Status=parsing | 抛 InvalidOperationException(Endpoint 转 422) |
| UT-QBI-10 | pageId=0 过滤有效 | FR-05 / AC-FR-05 | page_id=0,1,2 各 1 个 block | pageId=0 仅返回 1 个,验证 0 不被当作未传参 |
| UT-QBI-11 | blockType 过滤 | FR-06 / AC-FR-06 | text/image/table 各 1 个 | blockType=image 仅返回 1 个 image block |
| UT-QBI-12 | image block 返回 imageName/imagePath/imageUrl | FR-08 / AC-FR-08 | image block 关联 image 记录 | imageName/imagePath 非空,imageUrl 为 mock 的 presigned URL |
| UT-QBI-13 | text block 的图片字段为 null | FR-08 / AC-FR-08 | text block 无 image_id | imageName/imagePath/imageUrl 均为 null |
| UT-QBI-14 | image_id 有值但 image 记录缺失 | FR-08 / AC-FR-08 | block.image_id 非空,Include(Image) 返回 null | 图片字段为 null,记录警告日志 |
| UT-QBI-15 | OSS 生成 presigned URL 失败 | FR-08 / NFR-07 | GetPresignedUrlAsync 抛异常 | imageUrl=null,记录警告日志,其他字段正常返回 |
| UT-QBI-16 | 分页参数修正(pageSize>200) | NFR-04 / AC-FR-04 | pageSize=500 | pageSize 修正为 200 |
| UT-QBI-17 | blockData 合法 JSON 解析为对象 | FR-07 / AC-FR-07 | block_data='{"type":"text","text":"hello"}' | BlockData 为 JsonElement,非字符串 |
| UT-QBI-18 | blockData 非法 JSON 返回 null | FR-07 / AC-FR-07 | block_data='{ invalid' | BlockData=null,记录警告日志 |

### GetImageBlobAsync — 图片二进制

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-QBI-19 | imageId 不存在抛 KeyNotFoundException | FR-09 / AC-FR-09 | imageId 不在 DocumentParseImages | 抛 KeyNotFoundException(Endpoint 转 404) |
| UT-QBI-20 | OSS 下载失败抛异常 | FR-09 / AC-FR-09 / NFR-07 | DownloadAsync 抛异常 | 异常向上抛(Endpoint 转 500),记录 Error 日志 |
| UT-QBI-21 | 成功返回 ParseImageBlob | FR-09 / AC-FR-09 | image 记录存在,OSS 返回 Stream | 返回 ParseImageBlob,ImageName/ContentType/Stream 正确 |

### UpsertImportStatusAsync — 导入状态回写

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-QBI-22 | parse 不存在抛 KeyNotFoundException | FR-10 / AC-FR-10 | parseId 不存在 | 抛 KeyNotFoundException(Endpoint 转 404) |
| UT-QBI-23 | status 非法抛 ArgumentException | FR-10 / AC-FR-10 | status="invalid" | 抛 ArgumentException(Endpoint 转 400) |
| UT-QBI-24 | 首次插入 imported | FR-10 / AC-FR-10 | 无导入记录 | 调用 AddAsync,返回 importStatus=imported,UpdatedAt=null |
| UT-QBI-25 | 首次插入 failed | FR-10 / AC-FR-12 | 无导入记录 | 调用 AddAsync,返回 importStatus=failed |
| UT-QBI-26 | imported → imported 抛异常 | FR-11 / AC-FR-11 | 已有 imported 记录 | 抛 InvalidOperationException(Endpoint 转 422) |
| UT-QBI-27 | imported → failed 成功覆盖 | FR-12 / AC-FR-12 | 已有 imported 记录 | 调用 UpdateAsync,返回 importStatus=failed,UpdatedAt 非 null |
| UT-QBI-28 | failed → imported 成功覆盖 | FR-12 / AC-FR-12 | 已有 failed 记录 | 调用 UpdateAsync,返回 importStatus=imported |
| UT-QBI-29 | failed → failed 成功覆盖 | FR-12 / AC-FR-12 | 已有 failed 记录 | 调用 UpdateAsync,允许重复 failed |

## 集成测试(规划,本次不实现)

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| IT-QBI-01 | GET /importable 响应封装字段 | NFR-05 | 已有 parsed 记录 | 响应含 success/data/total/page/pageSize/totalPages |
| IT-QBI-02 | GET /blocks 404 | AC-FR-04 | parseId 不存在 | 404 + DOCLIBRARY_PARSE_NOT_FOUND |
| IT-QBI-03 | GET /blocks 422 | AC-FR-04 | parse.Status=parsing | 422 + DOCLIBRARY_PARSE_NOT_PARSED |
| IT-QBI-04 | GET /images/{id} 404 | AC-FR-09 | imageId 不存在 | 404 + DOCLIBRARY_IMAGE_NOT_FOUND |
| IT-QBI-05 | GET /images/{id} 500 | AC-FR-09 | OSS 下载失败 | 500 + DOCLIBRARY_OSS_DOWNLOAD_FAILED |
| IT-QBI-06 | POST /import-status 首次 200 | AC-FR-10 | 无导入记录 | 200,data.importStatus=imported |
| IT-QBI-07 | POST /import-status 重复 422 | AC-FR-11 | 已 imported | 422 + DOCLIBRARY_PARSE_ALREADY_IMPORTED |
| IT-QBI-08 | POST /import-status 参数非法 400 | AC-FR-10 | status=invalid | 400 + DOCLIBRARY_IMPORT_STATUS_INVALID |
| IT-QBI-09 | 内网直连可访问 | NFR-06 | 内网调用方无 Authorization 头 | 200（无鉴权，访问控制由网络隔离实现） |
| IT-QBI-10 | 并发写入 UNIQUE 约束 | NFR-08 | 并发 POST 同一 parseId | 最终仅一条记录,后到的转为 UPDATE 或 422 |

## 边界测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| BT-QBI-01 | parse 无 blocks | AC-FR-04 | status=parsed,blocks 表空 | 返回 data=[]、total=0 |
| BT-QBI-02 | parse 无 images | FR-08 | 所有 block 均无 image_id | 所有 block 的图片字段为 null |
| BT-QBI-03 | 无导入记录的 parse | AC-FR-03 | parse 无对应 import 记录 | importStatus=null、importedAt=null |
| BT-QBI-04 | block_data 空对象 | AC-FR-07 | block_data='{}' | BlockData 为空 JsonElement |
| BT-QBI-05 | block_data 空字符串 | AC-FR-07 | block_data='' | BlockData=null |
