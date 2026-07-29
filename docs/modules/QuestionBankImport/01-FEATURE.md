# 01-FEATURE — QuestionBank 只读解析数据接口

## 功能概述

DocLibrary 向 QuestionBank 提供已完成 MinerU 解析结果的只读查询。QuestionBank 拉取 parse、结构化 blocks 和图片后，在自身领域内完成拆题、入库和幂等控制。

DocLibrary 不记录 QuestionBank 的导入状态，也不保存 QuestionBank 题目 ID。QuestionBank 必须在自己的数据库事务中，以 DocLibrary `parseId` 作为来源幂等键，原子地完成题目写入和“来源已处理”记录。

## 数据流

```text
DocLibrary                              QuestionBank
document_parses ─┐
parse_blocks ────┼─ GET /internal/... ─► 拉取客户端
parse_images ────┘                       ├─ 拆题
                                         └─ 同一事务：写题目 + 记录 source parseId
```

## 验收条件

| 编号 | 验收条件 |
|------|----------|
| AC-01 | 三个接口全部为 GET，位于 `/internal/question-bank/*` |
| AC-02 | 接口只接受正确的 `X-DocLibrary-Service-Key` |
| AC-03 | parse 列表只返回 `status=parsed` 的记录并支持分页、文件名搜索 |
| AC-04 | blocks 查询支持 `pageId`、`blockType` 和分页 |
| AC-05 | image 查询以正确 MIME 返回 OSS 二进制流 |
| AC-06 | 不存在导入状态回写端点 |
| AC-07 | 响应不包含 `importStatus`、`importedAt` 或 QuestionBank 题目 ID |
| AC-08 | DocLibrary 不创建或更新 `document_parse_imports` |

## 接口

| 方法 | 路径 | 用途 |
|------|------|------|
| GET | `/internal/question-bank/document-parses` | 查询可供消费的 parsed parse |
| GET | `/internal/question-bank/document-parses/{parseId}/blocks` | 查询结构化 blocks |
| GET | `/internal/question-bank/images/{imageId}` | 获取解析图片 |

## 范围外

- QuestionBank 拉取客户端、拆题和入库实现
- QuestionBank 来源幂等表的具体结构
- DocLibrary 记录导入成功/失败
- DocLibrary 主动推送或事件发布
