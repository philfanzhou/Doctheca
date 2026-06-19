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
你是一个文档分析专家。分析以下文本片段，识别文档的学科、类型和结构特征。

学科类型：
- English：英语教材、阅读材料
- 语文：语文教材、文言文、现代文
- 数学：数学教材、习题集
- 物理：物理教材、实验报告
- 化学：化学教材、实验报告
- 生物：生物教材
- 其他：无法明确判断

文档类型：
- 教材：正式教学材料，包含章节、知识点讲解
- 知识点过关单：知识点列表，通常有编号
- 单词表：英文单词+中文释义的列表
- 短语表：英文短语+中文释义的列表
- 试卷：包含题号、选项的考试材料
- 其他：无法明确判断

分段策略：
- sentence：按完整句子分段（适合教材、阅读材料）
- concept：按概念/知识点分段（适合知识点过关单）
- word_entry：按词条分段（适合单词表、短语表）
- question：按题目分段（适合试卷）
- knowledge_point：按知识点分段（适合理科教材）

请分析以下文本并返回 JSON 格式的文档画像：

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

**Prompt 模板（以 English 教材为例）**：

```
你是一个英语教材分段专家。请将以下文本按完整句子分段。

分段规则：
1. 每个 segment 必须是一个完整的句子或紧密相关的句子群
2. 保持句子完整性，不要在句子中间断开
3. 如果句子过长（超过 100 词），可以在从句或连接词处适当断开
4. 保留原始文本，不要修改或改写

请返回 JSON 格式：
{
  "segments": [
    {"text": "第一段文本", "start_offset": 0, "end_offset": 50},
    {"text": "第二段文本", "start_offset": 51, "end_offset": 100}
  ]
}

注意：start_offset 和 end_offset 是相对于输入文本的字符偏移量。

{text_chunk}
```

**Prompt 模板（以数学教材为例）**：

```
你是一个数学教材分段专家。请将以下文本按知识点/概念分段。

分段规则：
1. 每个 segment 应包含一个完整的数学概念或公式及其解释
2. 公式（包括 LaTeX 格式）必须保持完整，不要拆断
3. 定义、定理、证明应作为整体，不要在中间断开
4. 例题和解答应保持在一起
5. 保留原始文本，不要修改或改写

请返回 JSON 格式：
{
  "segments": [
    {"text": "第一段文本", "start_offset": 0, "end_offset": 50},
    {"text": "第二段文本", "start_offset": 51, "end_offset": 100}
  ]
}

{text_chunk}
```

**Prompt 模板（以单词表为例）**：

```
你是一个单词表分段专家。请将以下文本按词条分段。

分段规则：
1. 每个 segment 包含一个完整的词条（单词 + 音标 + 释义 + 例句）
2. 如果一个单词有多个释义，将它们放在同一个 segment 中
3. 例句应与对应的单词放在同一个 segment 中
4. 保留原始文本，不要修改或改写

请返回 JSON 格式：
{
  "segments": [
    {"text": "abandon /əˈbændən/ v. 放弃；抛弃\nHe abandoned his plan.", "start_offset": 0, "end_offset": 60},
    {"text": "ability /əˈbɪləti/ n. 能力\nShe has the ability to solve problems.", "start_offset": 61, "end_offset": 120}
  ]
}

{text_chunk}
```

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

    private async Task<List<ParsedSegment>> SplitSentencesWithLlmAsync(
        string blockText,
        DocumentProfile profile,
        CancellationToken cancellationToken)
    {
        var segments = await _llmSegmentation.SegmentTextAsync(blockText, profile, cancellationToken);

        return segments.Select((seg, index) => new ParsedSegment
        {
            BlockId = $"b{index}",
            SentenceId = $"b{index}-s{index}",
            SegmentType = seg.SegmentType,
            Text = seg.Text,
            StartOffset = seg.StartOffset,
            EndOffset = seg.EndOffset,
            Tokens = Tokenize(seg.Text)
        }).ToList();
    }
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

用户只需配置 3 个必要参数：

```json
{
  "LlmSegmentation": {
    "ApiKey": "${LLM_API_KEY}",
    "BaseUrl": "https://api.siliconflow.cn/v1",
    "Model": "Qwen/Qwen2.5-7B-Instruct"
  }
}
```

其余参数由系统动态计算：

| 参数 | 计算方式 | 说明 |
|------|---------|------|
| MaxTokens | `context_length × 0.25` | 输出占上下文窗口 25% |
| ChunkSize | `(context_length - 200 - MaxTokens) × 1.5` | 输入占剩余空间，1.5 字符/token |
| Temperature | 写死 0.1 | 结构化输出任务的最佳实践 |
| MaxRetries | 写死 3 | 重试 3 次 |
| TimeoutSeconds | 写死 60 | 超时 60 秒 |

**动态参数获取流程**：

1. 服务启动时调用 `GET /models/{model_id}` 获取模型信息
2. 从响应中提取 `context_length`（上下文窗口大小）
3. 计算 MaxTokens 和 ChunkSize
4. 打印日志到控制台
5. 获取失败则使用安全默认值（MaxTokens=2048, ChunkSize=1500）

### 成本估算

假设一篇试卷 20 页、约 10000 字：
- 阶段一：1 次调用，约 500 token 输入 + 200 token 输出
- 阶段二：约 10 次调用（每 2 页一次），每次约 2000 token 输入 + 500 token 输出
- 总计：约 25000 token
- 成本（GPT-4o-mini）：约 ¥0.02-0.05
- 延迟：约 30-60 秒（串行）或 10-15 秒（并发）

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
提取原始文本（现有逻辑不变）
  │
  ▼
合并文本块（MergeTextIntoBlocks，现有逻辑不变）
  │
  ▼
阶段一：LLM 文档分析
  │  输入：前 2000 字
  │  输出：DocumentProfile（学科+类型+分段策略）
  │
  ▼
阶段二：LLM 智能分段
  │  输入：文本块 + DocumentProfile
  │  输出：List<SegmentResult>
  │
  ▼
构建 ParsedSegment（现有逻辑不变）
  │
  ▼
Tokenize（现有逻辑不变）
  │
  ▼
写入 DB / OpenSearch（现有逻辑不变）
```

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
