# DocumentManagement — 测试文档

## 单元测试（`DocumentFileServiceTests/` 文件夹）

按操作分组，每个场景一个测试类：

| # | 测试方法 | 覆盖 | 状态 | 所在文件 |
|---|---------|------|------|---------|
| UT-DF-01 | `CreateAsync_SetsCreatedAtAndReturnsModel` — 创建文件设置 CreatedAt 并返回模型 | FR-01 | completed | `DocumentFileServiceCreateTests.cs` |
| UT-DF-02 | `GetByIdAsync_ReturnsModel_WhenExists` — 存在时返回模型 | FR-03 | completed | `DocumentFileServiceGetByIdTests.cs` |
| UT-DF-03 | `GetByIdAsync_ReturnsNull_WhenNotExists` — 不存在时返回 null | FR-03 | completed | `DocumentFileServiceGetByIdTests.cs` |
| UT-DF-04 | `GetListAsync_ReturnsPagedResults` — 分页返回结果 | FR-02 | completed | `DocumentFileServiceListTests.cs` |
| UT-DF-05 | `DeleteAsync_DelegatesToRepository` — 删除委托给仓储 | FR-05 | completed | `DocumentFileServiceDeleteTests.cs` |

## 单元测试（DocumentFileDeleteCleanupTests.cs）

| # | 测试方法 | 覆盖 | 状态 |
|---|---------|------|------|
| UT-DF-06 | `Cleanup_CollectsAllOssPaths_BeforeDeletion` — 删除前收集所有 OSS 路径（源文件 + zip + 图片） | FR-05 | completed |
| UT-DF-07 | `Cleanup_HandlesNullZipPath` — 处理 ZipPath 为 null 的情况（如失败解析） | FR-05 | completed |
| UT-DF-08 | `Cleanup_DedupesDuplicatePaths` — 重复路径去重（HashSet） | FR-05 | completed |

## FR/AC 映射

| FR | 覆盖测试 | 覆盖 AC |
|----|---------|---------|
| FR-01 上传 | UT-DF-01 | AC-01 |
| FR-02 列表 | UT-DF-04 | AC-05 |
| FR-03 详情 | UT-DF-02, UT-DF-03 | AC-06 |
| FR-04 更新元数据 | —（无直接 UT，需 IT） | AC-10 |
| FR-05 删除联动 | UT-DF-05, UT-DF-06, UT-DF-07, UT-DF-08 | AC-11, AC-12 |
| FR-06 触发解析 | —（无直接 UT，需 IT） | AC-07, AC-08, AC-09 |

> 上传校验（AC-02/03/04）和触发解析校验（AC-07/08/09）当前无单元测试覆盖，需集成测试验证。

## 集成测试（规划中）

| # | 测试用例 | 覆盖 |
|---|---------|------|
| IT-DF-01 | 上传 PDF 文件成功，DB 有记录，OSS 有文件 | AC-01 |
| IT-DF-02 | 上传不支持格式返回 400 + DOCLIBRARY_FILE_FORMAT_UNSUPPORTED | AC-02 |
| IT-DF-03 | 上传空文件返回 400 + DOCLIBRARY_FILE_REQUIRED | AC-03 |
| IT-DF-04 | 上传超 200MB 返回 400 | AC-04 |
| IT-DF-05 | 列表分页 + fileName 搜索 + parseStatus 过滤 | AC-05 |
| IT-DF-06 | 详情返回 parses + 图片 presigned URL | AC-06 |
| IT-DF-07 | 触发解析创建 pending 记录 | AC-07 |
| IT-DF-08 | 重复触发同 modelVersion 返回 422 | AC-08 |
| IT-DF-09 | MinerU 未配置时触发返回 503 | AC-09 |
| IT-DF-10 | 更新元数据后 OpenSearch 索引同步 | AC-10 |
| IT-DF-11 | 删除文件清理 DB + OSS + OpenSearch | AC-11 |
| IT-DF-12 | 删除不存在文件返回 404 | AC-12 |

## 运行方式

```bash
# 运行本模块相关单元测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentFileServiceTests"
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests --filter "FullyQualifiedName~DocumentFileDeleteCleanup"

# 运行所有测试
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests
```

## 覆盖率目标

- `DocumentFileService` 代码行覆盖率 ≥ 80%
- `DocumentFileRepository` CRUD 方法 100% 覆盖
- 删除联动路径收集逻辑 100% 覆盖
