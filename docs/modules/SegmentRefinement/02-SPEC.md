# 02-SPEC — SegmentRefinement 需求规格

## REQ-REFINE-01: 持久化 DocumentProfile

### 需求描述

文档解析完成后，LLM 分析结果（DocumentProfile）必须持久化到数据库，以便管理界面展示。

### 数据模型

**documents 表新增字段：**

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| llm_profile_json | text | nullable | LLM 分析结果 JSON |

**JSON 结构：**
```json
{
  "subject": "English",
  "docType": "教材",
  "segmentStrategy": "sentence",
  "structure": {
    "hasChapters": true,
    "hasQuestions": false,
    "hasWordList": false,
    "hasFormulas": false
  }
}
```

### 验收场景

- GIVEN 文档通过 LLM 分段入库，WHEN 查询 documents 表，THEN llm_profile_json 字段包含正确的 JSON
- GIVEN LLM 未配置（回退到规则切割），WHEN 查询 documents 表，THEN llm_profile_json 为 NULL

---

## REQ-REFINE-02: 获取文档 Segments API

### 需求描述

提供 Admin API 获取指定文档的所有 segments 和 LLM 分析结果。

### API 设计

```
GET /admin/documents/{id}/segments
```

**响应：**
```json
{
  "success": true,
  "data": {
    "documentId": "uuid",
    "title": "文档标题",
    "status": "ready",
    "profile": {
      "subject": "English",
      "docType": "教材",
      "segmentStrategy": "sentence",
      "structure": { "hasChapters": true, "hasQuestions": false, "hasWordList": false, "hasFormulas": false }
    },
    "segments": [
      {
        "id": "uuid",
        "sentenceId": "p1-s1",
        "segmentType": "sentence",
        "text": "segment 文本内容",
        "startOffset": 0,
        "endOffset": 50,
        "pageNumber": 1
      }
    ],
    "totalCount": 47
  }
}
```

### 验收场景

- GIVEN 文档 ID 存在且状态为 ready，WHEN 调用 API，THEN 返回 200 + segments 列表
- GIVEN 文档 ID 不存在，WHEN 调用 API，THEN 返回 404
- GIVEN 文档状态为 processing，WHEN 调用 API，THEN 返回 422

---

## REQ-REFINE-03: 提交修正并重新拆分 API

### 需求描述

管理员提交修正后的 segments，系统将其作为 few-shot examples 发给 LLM，重新分析并拆分整个文档。

### API 设计

```
POST /admin/documents/{id}/refine
```

**请求体：**
```json
{
  "corrections": [
    {
      "originalSentenceIds": ["p1-s1", "p1-s2"],
      "action": "merge",
      "newText": "合并后的文本",
      "newSegmentType": "sentence"
    },
    {
      "originalSentenceIds": ["p1-s3"],
      "action": "split",
      "splitPosition": 25,
      "newSegmentType": "concept"
    },
    {
      "originalSentenceIds": ["p1-s4"],
      "action": "retype",
      "newSegmentType": "concept"
    }
  ]
}
```

**action 类型：**
- `merge`：合并多条 segment 为一条
- `split`：将一条 segment 在指定位置拆分为两条
- `retype`：修改 segment 的类型
- `splitMerge`：拆分一条 segment，并将两部分分别与相邻 segment 合并（一步完成复杂重组）

**splitMerge 示例：**
```json
{
  "originalSentenceIds": ["p1-s3"],
  "action": "splitMerge",
  "splitPosition": 25,
  "mergeFirstWithPrevious": true,
  "mergeSecondWithNext": true
}
```
效果：在位置 25 拆分段落 B，前半部分与上一段 A 合并，后半部分与下一段 C 合并。

**响应：**
```json
{
  "success": true,
  "data": {
    "documentId": "uuid",
    "backupId": "uuid",
    "message": "Refinement queued, re-processing started",
    "correctionCount": 3
  }
}
```

### 验收场景

- GIVEN 有效的修正请求，WHEN 调用 API，THEN 备份旧 segments + 启动重新拆分
- GIVEN 修正数量超过 20 条，WHEN 调用 API，THEN 返回 400
- GIVEN 文档不在 ready 状态，WHEN 调用 API，THEN 返回 422

---

## REQ-REFINE-04: LLM Few-Shot Refinement

### 需求描述

将用户修正的 segments 作为 few-shot examples 构造 prompt，让 LLM 理解正确的分段方式后重新分析文档。

### Prompt 设计

```
你是一个文档分段专家。以下是用户修正后的正确分段示例：

示例 1:
原文: "合并后的文本内容..."
正确分段: [{"text": "合并后的文本内容...", "segment_type": "sentence"}]

示例 2:
原文: "需要拆分的文本..."
正确分段: [{"text": "前半部分", "segment_type": "concept"}, {"text": "后半部分", "segment_type": "concept"}]

请参照以上示例的分段方式，对以下文档进行分段。注意保持与示例一致的分段风格和类型选择。

文档信息：学科={subject}，类型={docType}，策略={strategy}

{text_chunk}
```

### 验收场景

- GIVEN 用户提交了 3 条修正，WHEN 构造 prompt，THEN prompt 包含 3 个 few-shot examples
- GIVEN LLM 返回新的分段结果，WHEN 解析结果，THEN 返回有效的 segments 列表
- GIVEN 某页 LLM 调用失败，WHEN 该页 refine 异常，THEN 创建新实体复制原有 segment 内容（不回滚），其余页正常继续

---

## REQ-REFINE-05: Segment 备份与回滚

### 需求描述

重新拆分前自动备份当前 segments，失败时可回滚。

### 数据模型

**document_segment_backups 表：**

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| id | uuid | PK | 备份 ID |
| document_id | uuid | FK -> documents | 关联文档 |
| backup_data | text | not null | JSON 格式的完整 segments 数据 |
| correction_count | int | not null | 本次修正的数量 |
| created_at | timestamp | not null | 备份时间 |

### 验收场景

- GIVEN 提交修正请求，WHEN 开始重新拆分，THEN 自动创建备份记录
- GIVEN 重新拆分失败，WHEN 触发回滚，THEN 从备份恢复 segments + occurrences
- GIVEN 回滚成功，WHEN 查询 segments，THEN 数据与备份时一致

---

## REQ-REFINE-06: 前端 Segment 管理界面

### 需求描述

在管理后台新增文档分段管理页面，展示 LLM 分析结果和 segments 列表，支持交互式修正。

### 页面布局

```
┌──────────────────────────────────────────────────────────────┐
│  文档：高一英语必修一 Unit1 Reading           [状态: ready]    │
│                                                              │
│  ┌─ LLM 分析结果 ─────────────────────────────────────────┐  │
│  │ 科目: English  类型: 教材  策略: sentence                │  │
│  │ 结构: 章节 ✓  题目 ✗  单词表 ✗  公式 ✗                  │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  Segments (共 47 条)              [全选] [合并选中] [重新拆分] │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ☐ p1-s1  [sentence]  📄 P1                         │  │
│  │   "My name is Li Hua, a student from China."          │  │
│  │   [改类型]                                              │  │
│  │                                                        │  │
│  │ ☐ p1-s2  [sentence]  📄 P1                         │  │
│  │   "I'm writing to tell you about our school life."    │  │
│  │   [改类型]                                              │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  [提交修正给 LLM 学习 → 重新拆分入库]                          │
└──────────────────────────────────────────────────────────────┘
```

### 交互操作

| 操作 | 触发方式 | 行为 |
|------|---------|------|
| 选中 segment | 点击 checkbox | 添加到选中列表 |
| 合并 | 选中 2+ 条 → 点击"合并选中" | 将选中的 segments 合并为一条，文本拼接 |
| 拆分 | 点击 segment → 点击"拆分" → 输入位置 | 在指定位置拆分为两条 |
| 改类型 | 点击 segment 的类型标签 | 弹出下拉框选择新类型 |
| 撤销 | 点击"撤销" | 清除所有修正并从服务器重新加载 segments（逐个撤销不安全，剩余修正引用的 sentenceId 在重载后已过期） |
| 提交 | 点击"提交修正" | 收集所有修正 → 调用 refine API |

### 验收场景

- GIVEN 文档状态为 ready，WHEN 打开页面，THEN 显示 LLM 分析结果和 segments 列表
- GIVEN 选中 2 条 segment，WHEN 点击合并，THEN 显示合并后的预览
- GIVEN 点击 segment 的类型标签，WHEN 选择新类型，THEN 显示更新后的类型
- GIVEN 有未提交的修正，WHEN 点击提交，THEN 调用 refine API 并显示进度

---

## 非功能需求

| 需求 | 说明 |
|------|------|
| 性能 | 获取 segments API 响应 < 500ms |
| 性能 | 重新拆分耗时取决于文档大小，需显示进度 |
| 安全 | 仅管理员可访问（RequireAuthorization） |
| 可靠性 | LLM 失败的页面自动保留原有分段内容（新建实体），备份单独保存（fire-and-forget），不丢失数据 |
| 可用性 | 所有操作可一次性撤销，提交前显示预览 |
