# 07-LLM-SEGMENTATION — LLM 智能文档分段设计

## 背景与动机

### 当前问题

现有 `DocumentParserService.SplitSentences` 使用手写规则（`.!?` + 空格）进行句子切割，存在以下问题：

1. **规则过于简单**：仅识别 `.!?` 后跟空格的模式，无法处理复杂文档结构
2. **学科适配差**：数学公式、物理推导、化学方程式等被错误切割
3. **文档类型单一**：教材、试卷、单词表、知识点过关单使用同一套切割规则
4. **长段落问题**：无标点的长段落整段返回为一个 segment，搜索结果上下文过长
5. **缩写词表硬编码**：仅 12 个缩写，覆盖不全

### 设计目标

- **准确性优先**：宁可入库慢一些，也要保证分段准确
- **学科感知**：不同学科使用不同的分段策略
- **文档类型感知**：教材、试卷、单词表等使用不同分段逻辑
- **上下文理解**：LLM 看到文档整体后做出分段决策

## 方案概述

采用**两阶段 LLM 处理**策略：

```
阶段一：文档分析（1 次 LLM 调用）
  → 识别学科、文档类型、结构特征

阶段二：智能分段（N 次 LLM 调用，按章节/页面分块）
  → 基于文档画像，按语义单元分段
```

## 详细设计

### 阶段一：文档分析

**输入**：文档前 2000 字（约 3-5 页）

**输出**：文档画像 JSON

```json
{
  "subject": "English|语文|数学|物理|化学|生物|其他",
  "grade": "K|G1-G12|空字符串（无法判断时）",
  "year": "2024|2025|空字符串（无法判断时）",
  "docType": "教材|知识点过关单|单词表|短语表|试卷|其他",
  "structure": {
    "hasChapters": true,
    "hasQuestions": false,
    "hasWordList": false,
    "hasFormulas": false
  },
  "segmentStrategy": "sentence|concept|word_entry|question|knowledge_point"
}
```

**Prompt 设计**：

```
你是一个文档分析专家。分析以下文本片段，识别文档的学科、年级、年份、类型和结构特征。

学科类型：
- English、语文、数学、物理、化学、生物、其他

年级（从标题或内容推断）：
- K：幼儿园/学前
- G1-G12：小学一年级到高三
- 无法判断时返回空字符串

年份（从标题、页眉、版权页等推断，格式为4位数字如"2024"）：
- 无法判断时返回空字符串

文档类型：
- 教材、知识点过关单、单词表、短语表、试卷、其他

分段策略：
- sentence、concept、word_entry、question、knowledge_point

请返回 JSON：
{
  "subject": "学科",
  "grade": "年级",
  "year": "年份",
  "doc_type": "文档类型",
  "segment_strategy": "分段策略",
  "structure": { "has_chapters": bool, "has_questions": bool, "has_word_list": bool, "has_formulas": bool }
}

{text_preview}
```

### 阶段二：智能分段

**输入**：
- 文档画像（阶段一输出）
- 文本分块（2000-3000 字）

**输出**：分段结果 JSON 数组

```json
{
  "segments": [
    {
      "text": "分段后的文本内容",
      "start_offset": 0,
      "end_offset": 50,
      "segment_type": "sentence|concept|word_entry|question|knowledge_point"
    }
  ]
}
```

**分段策略矩阵**：

| 学科 | 文档类型 | 分段策略 | 分段规则 |
|------|---------|---------|---------|
| English | 教材 | sentence | 按完整句子分段，保持句子完整性 |
| English | 单词表 | word_entry | 每个词条（单词+释义+例句）为一个 segment |
| English | 短语表 | word_entry | 每个短语条目为一个 segment |
| English | 试卷 | question | 每道题（题干+选项）为一个 segment |
| 语文 | 教材 | sentence | 按完整句子分段，注意 `。` `？` `！` 结尾 |
| 数学 | 教材 | concept | 按知识点/公式+解释为单位，公式不拆断 |
| 数学 | 试卷 | question | 每道题为一个 segment |
| 物理 | 教材 | concept | 按概念解释为单位，公式和文字描述保持在一起 |
| 化学 | 教材 | concept | 按概念解释为单位，化学方程式保持完整 |
| 生物 | 教材 | concept | 按概念解释为单位 |
| * | 知识点过关单 | knowledge_point | 每个知识点条目为一个 segment |

**系统 Prompt**：`直接返回JSON，不要解释。`

**分段 Prompt**（精简版，按策略动态生成）：

```
按 {segmentStrategy} 策略分段。{strategyHint}
返回JSON，保留原文，offset为字符偏移量。

{"segments":[{"text":"...","start_offset":0,"end_offset":10,"segment_type":"..."}]}

---
{text}
---
```

其中 `strategyHint` 根据策略不同：
- `word_entry`：每个词条（单词+释义）为一个segment。
- `question`：每道题（题干+选项）为一个segment。
- `concept`：每个概念/公式为一个segment。
- `sentence`：每个完整句子为一个segment。

> **设计原则**：prompt 越短，LLM 思考时间越少。分析阶段已确定文档类型和策略，分段阶段只需执行。

## 技术实现

### 新增接口

```csharp
public interface ILlmSegmentationService
{
    /// <summary>
    /// 分析文档类型和结构特征
    /// </summary>
    Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于文档画像对文本进行智能分段
    /// </summary>
    Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken = default);
}

public record DocumentProfile
{
    public string Subject { get; init; }      // 学科
    public string Grade { get; init; }        // 年级: K, G1-G12
    public string Year { get; init; }         // 年份: 2024, 2025
    public string DocType { get; init; }      // 文档类型
    public string SegmentStrategy { get; init; } // 分段策略
    public DocumentStructure Structure { get; init; }
}

public record DocumentStructure
{
    public bool HasChapters { get; init; }
    public bool HasQuestions { get; init; }
    public bool HasWordList { get; init; }
    public bool HasFormulas { get; init; }
}

public record SegmentResult
{
    public string Text { get; init; }
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public string SegmentType { get; init; }
}
```

### 修改 DocumentParserService

```csharp
public class DocumentParserService : IDocumentParserService
{
    private readonly ILlmSegmentationService _llmSegmentation;

    // 构造函数注入 ILlmSegmentationService

    public async Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken)
    {
        // 1. 提取原始文本（现有逻辑不变）
        // 2. 合并文本块（现有逻辑不变）
        // 3. 调用 LLM 分段（替换 SplitSentences）
        // 4. 构建 ParsedDocument（现有逻辑不变）
    }

    // 按容量切块 + LLM 分段，详见 03-DESIGN.md 中的 ChunkByCapacity 设计
    // SentenceId 格式：p{pageNumber}-s{segmentIndex}
}
```

### LLM 调用实现

```csharp
public class LlmSegmentationService : ILlmSegmentationService
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    public async Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken)
    {
        var prompt = BuildAnalysisPrompt(textPreview);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseDocumentProfile(response);
    }

    public async Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken)
    {
        var prompt = BuildSegmentationPrompt(text, profile);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseSegmentResults(response);
    }

    private async Task<string> CallLlmAsync(string prompt, CancellationToken cancellationToken)
    {
        // 调用 LLM API（OpenAI/Claude/本地模型）
        // 支持重试和超时处理
    }
}
```

## 配置与扩展

### LLM 配置

用户需配置以下参数：

```json
{
  "LlmSegmentation": {
    "ApiKey": "your-api-key",
    "BaseUrl": "https://api.xiaomimimo.com/v1",
    "Model": "mimo-v2.5-pro",
    "ContextLength": "1M",
    "MaxTokens": "128K",
    "TimeoutSeconds": 300
  }
}
```

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| ApiKey | string | 空（禁用 LLM） | API 密钥，为空时禁用 LLM 分段 |
| BaseUrl | string | 空 | API 基础 URL |
| Model | string | 空 | 模型 ID |
| ContextLength | string | 空 | 上下文窗口，支持 "128K"、"1M" 格式 |
| MaxTokens | string | "4K" | 最大输出 token，支持 "4K"、"128K" 格式 |
| TimeoutSeconds | int | 300 | HTTP 超时秒数 |
| Temperature | double | 0.1 | 固定值，不可配置 |
| MaxRetries | int | 3 | 固定值，不可配置 |

**ChunkSize 计算**：

```
ChunkSize = min((ContextLength - 200 - MaxTokens) × 0.8 × 1.5, 5000)
```

- 上限 5000 字符（避免单次 LLM 调用超时）
- 200 = 系统 prompt 预留 token
- 0.8 = 安全系数
- 1.5 = 字符/token 比率

**初始化流程**：

1. 启动时解析 ContextLength 和 MaxTokens 配置
2. 若 ContextLength 未配置，尝试 `GET /models/{model_id}` 获取
3. 计算 ChunkSize 并打印日志
4. 若 ContextLength 不可用，禁用 LLM 分段（ChunkSize=0）

### 成本估算

假设一篇试卷 20 页、约 10000 字（ChunkSize=5000）：
- 阶段一（分析）：1 次调用，约 2000 字符输入
- 阶段二（分段）：约 2 次调用（ChunkByCapacity 切块），并行执行
- 总计：3 次 LLM 调用
- 延迟：并行执行，总耗时约等于最慢的单次调用（30-60 秒）

### 错误处理

| 错误类型 | 处理策略 |
|---------|---------|
| LLM API 超时 | 重试 3 次，失败后回退到规则切割 |
| LLM 返回格式异常 | 解析失败后重试，失败后回退到规则切割 |
| LLM 返回空结果 | 回退到规则切割 |
| 网络连接失败 | 回退到规则切割 |

### 回退机制

当 LLM 调用失败时，回退到现有的 `SplitSentences` 规则切割：

```csharp
private async Task<List<ParsedSegment>> SplitSentencesWithFallbackAsync(
    string blockText,
    DocumentProfile profile,
    CancellationToken cancellationToken)
{
    try
    {
        return await SplitSentencesWithLlmAsync(blockText, profile, cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "LLM 分段失败，回退到规则切割");
        return SplitSentences(blockText); // 现有规则切割
    }
}
```

## 数据流

```
文档上传
  │
  ▼
提取页面文本（PDF 逐页 / Word 分页检测 / PPT 逐 slide）
  │  输出：List<(PageNumber, Text)>
  │
  ▼
阶段一：LLM 文档分析
  │  输入：前 2000 字
  │  输出：DocumentProfile（学科+类型+分段策略）
  │
  ▼
按容量切块（ChunkByCapacity）
  │  输入：页面文本 + ChunkSize
  │  逻辑：拼接页面 → 按 ChunkSize 切块 → 优先在段落边界断开
  │  输出：List<TextChunk>（含 offset→page 映射）
  │
  ▼
阶段二：LLM 智能分段（每个 chunk 1 次调用）
  │  输入：chunk.Text + DocumentProfile
  │  输出：List<SegmentResult>
  │
  ▼
回映射到页码（MapOffsetToPage）
  │  每个 segment 的 offset → 对应 PageNumber
  │
  ▼
构建 ParsedSegment（SentenceId = p{N}-s{K}）
  │
  ▼
Tokenize（现有逻辑不变）
  │
  ▼
写入 DB / OpenSearch（现有逻辑不变）
```

**Refine 流程共享同一套切块 + 回映射逻辑**，区别仅在于：
- 初始拆分调用 `SegmentTextAsync`，refine 调用 `RefineSegmentTextAsync`
- 初始拆分从文件提取页面文本，refine 从已有 segment text 拼回

## 测试策略

### 单元测试

| 测试场景 | 验证点 |
|---------|-------|
| 文档分析 - English 教材 | 正确识别 subject=English, docType=教材, strategy=sentence |
| 文档分析 - 数学试卷 | 正确识别 subject=数学, docType=试卷, strategy=question |
| 文档分析 - 单词表 | 正确识别 subject=English, docType=单词表, strategy=word_entry |
| 智能分段 - 完整句子 | 不在句子中间断开 |
| 智能分段 - 数学公式 | 公式保持完整 |
| 智能分段 - 单词表 | 每个词条为一个 segment |
| 错误处理 - LLM 超时 | 回退到规则切割 |
| 错误处理 - 格式异常 | 重试后回退 |

### 集成测试

| 测试场景 | 验证点 |
|---------|-------|
| 完整流程 - English 教材 | 分段结果符合英语句子边界 |
| 完整流程 - 数学试卷 | 每道题为一个 segment |
| 完整流程 - 单词表 | 每个词条为一个 segment |
| 性能测试 | 20 页文档入库时间 < 2 分钟 |

## 迁移策略

### 已有文档

已有文档需要重新入库才能使用新的分段逻辑。提供管理员重试接口：

```
POST /admin/documents/{id}/retry
```

### 渐进式部署

1. **阶段一**：实现 LLM 分段服务，但默认关闭（配置开关）
2. **阶段二**：在测试环境验证，调整 Prompt
3. **阶段三**：生产环境灰度发布，先对新文档启用
4. **阶段四**：对已有文档批量重新入库

## 监控与告警

### 关键指标

| 指标 | 说明 | 告警阈值 |
|------|------|---------|
| LLM 调用成功率 | 成功调用次数 / 总调用次数 | < 95% |
| LLM 调用延迟 | 单次调用耗时 | > 30s |
| 回退率 | 回退到规则切割的比例 | > 10% |
| 分段质量 | 人工抽样评估 | - |

### 日志

```
[INFO] 文档分析完成：DocumentId={id}, Subject={subject}, DocType={docType}, Strategy={strategy}
[INFO] LLM 分段完成：DocumentId={id}, ChunkCount={count}, SegmentCount={segmentCount}
[WARN] LLM 分段失败，回退到规则切割：DocumentId={id}, Error={error}
[ERROR] LLM 调用失败：DocumentId={id}, RetryCount={retryCount}, Error={error}
```

## 未来扩展

1. **多语言支持**：扩展 Prompt 支持更多语言
2. **自定义分段策略**：允许用户上传时指定分段策略
3. **分段质量反馈**：用户可以标记分段不准确的 segment，用于优化 Prompt
4. **批量重新入库**：支持批量对已有文档重新分段
5. **本地模型支持**：支持部署本地 LLM，降低 API 调用成本
