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
    "TimeoutSeconds": 600
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
| TimeoutSeconds | int | 1800 | 单次尝试硬总超时秒数（30 分钟），代码内置默认值 |
| StreamIdleTimeoutSeconds | int | 60 | SSE 流式空闲超时秒数，代码内置默认值 |
| MaxConcurrency | int | 2 | 分段阶段最大并发 LLM 调用数，代码内置默认值 |
| Temperature | double | 0.1 | 固定值，不可配置 |
| MaxRetries | int | 3 | 固定值，不可配置 |

**ChunkSize 计算**：

```
ChunkSize = min((ContextLength - 200 - MaxTokens) × 0.8 × 1.5, 2500)
```

- 上限 2500 字符（控制单次 LLM 输出量，避免 word_entry 等高输出策略单次生成时间过长）
- 200 = 系统 prompt 预留 token
- 0.8 = 安全系数
- 1.5 = 字符/token 比率

> **上限从 5000 调整为 2500 的原因**：word_entry 策略下每个词条约产生 80 字符 JSON 输出，5000 字符 chunk 可达 ~145 条目 ≈ 11600 字符输出，LLM 生成耗时过长。2500 字符 chunk 约 70 条目 ≈ 5600 字符输出，单次调用更快，且更多 chunk 可并行执行。

**初始化流程**：

1. 启动时解析 ContextLength 和 MaxTokens 配置
2. 若 ContextLength 未配置，尝试 `GET /models/{model_id}` 获取
3. 计算 ChunkSize 并打印日志
4. 若 ContextLength 不可用，禁用 LLM 分段（ChunkSize=0）

### 流式响应（SSE）优化

**背景问题**：非流式调用下，LLM 在生成完整响应前不返回任何数据，HttpClient 在 300s 内未收到响应即超时。mimo-v2.5-pro 对 word_entry 等高输出策略单次生成可达 3-6 分钟，频繁触发 300s 超时。

**解决方案**：启用 OpenAI 兼容的 SSE 流式响应（`stream: true`），LLM 边生成边推送 token，客户端持续读取，连接保持活跃，避免空闲超时。

**实现要点**：

1. 请求体增加 `stream: true`
2. 使用 `HttpRequestMessage` + `HttpClient.SendAsync(..., HttpCompletionOption.ResponseHeadersRead)` 开始流式读取。注意：`PostAsJsonAsync` 会缓冲完整响应，不能用于 SSE
3. 禁用 `HttpClient.Timeout`（设为 `InfiniteTimeSpan`），改用 `CancellationTokenSource` 控制单次尝试超时
4. 逐行读取 SSE 事件（`data: {...}`），累积 `choices[0].delta.content` 拼接完整响应
5. 遇到 `data: [DONE]` 结束读取
6. **Chunk 处理支持受控并行**：通过 `MaxConcurrency` 控制并发 LLM 调用数（默认 2），使用 `SemaphoreSlim` 限制。推理模型（如 mimo-v2.5-pro）"思考"阶段耗时长，并发可重叠思考时间，总耗时约降低到 `串行总耗时 / MaxConcurrency`

**SSE 事件格式**：

```
data: {"choices":[{"delta":{"content":"{"}}]}

data: {"choices":[{"delta":{"content":"\"segments\":"}}]}

data: [DONE]
```

**超时控制**：

采用双层超时策略：

1. **SSE 空闲超时（主要机制）**：每次 `ReadLineAsync` 单独创建一个 `CancellationTokenSource`，在 `StreamIdleTimeoutSeconds` 内未收到新的 SSE 事件行时取消
   - 默认 60 秒
   - 只要 LLM 持续发送 token（即使很慢），调用就不会被中断
   - 真正卡死/断流时才触发，误杀率远低于总时长超时

2. **硬总超时（安全网）**：每次尝试创建 `CancellationTokenSource(TimeoutSeconds)`，覆盖从请求发起到流式读取完成的整个周期
   - 默认 1800 秒（30 分钟）
   - 防止极端情况（如 provider 持续发送无意义 keep-alive）无限挂起

- `HttpClient.Timeout = InfiniteTimeSpan`（禁用 HttpClient 级绝对超时）

### Offset 计算优化

**LLM 不再计算 offset，由代码精确计算。**

旧方案让 LLM 返回 `start_offset` 和 `end_offset`，但推理模型（如 mimo-v2.5-pro）会花大量"思考时间"计算字符偏移，且结果经常出错需要代码修正。

新方案：
1. Prompt 只要求 LLM 返回 `text` 和 `segment_type`，不要求 offset
2. 代码通过 `IndexOf` 在原文中按顺序定位每个 segment 的精确偏移
3. 输出 token 大幅减少（每条目减少 ~30 字符的 offset 数据）
4. 推理模型的思考时间大幅减少（无需计算字符位置）
5. Offset 准确率 100%（代码计算 vs LLM 猜测）

### 成本估算

假设一篇试卷 20 页、约 10000 字（ChunkSize=2500）：
- 阶段一（分析）：1 次调用，约 2000 字符输入
- 阶段二（分段）：约 4 次调用（ChunkByCapacity 切块），MaxConcurrency=2 并行执行
- 总计：5 次 LLM 调用
- 延迟：并发执行，总耗时约等于各批次中最慢的调用之和；7 个 chunk / 2 并发 ≈ 4 批次

### 错误处理

| 错误类型 | 处理策略 |
|---------|---------|
| LLM API 超时（CancellationToken 触发） | 重试 3 次，仍失败则进入"策略感知回退"判定 |
| SSE 流读取中断 | 重试 3 次，仍失败则进入"策略感知回退"判定 |
| LLM 返回格式异常 | 解析失败后重试，仍失败则进入"策略感知回退"判定 |
| LLM 返回空结果 | 直接进入"策略感知回退"判定 |
| 网络连接失败 | 重试 3 次，仍失败则进入"策略感知回退"判定 |

#### 策略感知回退

回退到 `SplitSentences` 规则切割**仅适用于 sentence 策略**（英语/语文教材、阅读材料等含句末标点的文档）。对于其它策略，规则切割会把整段文本当成一个 segment 入库，污染搜索结果，因此**不静默回退**，而是抛异常触发任务级失败（`FailIngestionJobAsync`），由管理员或自动重试机制处理。

| 策略 | 错误处理行为 |
|------|-------------|
| `sentence` | 回退到 `SplitSentences`（仅取包含 `.!? + 空格` 边界的句子） |
| `word_entry` | 不回退，记录 `LogError` 并抛 `InvalidOperationException` 触发任务失败 |
| `concept` | 不回退，记录 `LogError` 并抛 `InvalidOperationException` 触发任务失败 |
| `question` | 不回退，记录 `LogError` 并抛 `InvalidOperationException` 触发任务失败 |
| `knowledge_point` | 不回退，记录 `LogError` 并抛 `InvalidOperationException` 触发任务失败 |

> **设计理由**：单词表、短语表、试卷、知识点过关单等结构化文档只能由 LLM 正确切分。规则切割产生的"整段单条记录"比"无记录"更糟，因为它会污染搜索索引和精确检索结果。触发任务级失败后，管理员可以通过 `POST /admin/documents/{id}/retry` 重试，或检查 LLM 服务健康状态。

#### 防御性过滤

LLM 在 prompt 含糊时偶尔会输出"摘要/标题"型整块 segment（`text.Length` 接近 `originalText.Length`），与正常 word_entry 混在一起被一起入库。`DocumentParserService` 在回映射 segment 到页面时丢弃这类可疑 segment：

- 触发条件：`segments.Count > 1`（必须有多个 segment 才过滤，孤立 1 个 segment 可能是合法短文本输出）且 `seg.Text.Length >= chunk.Text.Length * 0.9`
- 处理动作：`LogWarning` 后跳过该 segment
- 阈值取 `0.9` 而非 `1.0` 是为了容忍 LLM 偶尔追加空格/换行的边界情况

#### Question 检测策略限制

`ExtractQuestions` 使用 `QuestionNumberRegex`（`^\s*(\d+)\s*[.、．)\]】]`）识别题号，但该正则也会匹配单词表/短语表中的编号词条（如 `1. shake /ʃeɪk/ ...`），导致整页内容被误聚合为一条 question segment。

**策略限制**：`ExtractQuestions` 仅在 `SegmentStrategy == SegmentTypes.Question` 时执行。对于 `word_entry`、`concept`、`knowledge_point`、`sentence` 等非 question 策略，跳过 Question 检测，不写入 `question_segments` 表。

| 策略 | Question 检测 | 原因 |
|------|--------------|------|
| `question` | 执行 | 试卷/练习册，编号行是真正的题目 |
| `sentence` | 不执行 | 教材/阅读材料，编号行是段落序号 |
| `word_entry` | 不执行 | 单词表/短语表，编号行是词条序号 |
| `concept` | 不执行 | 知识点文档，编号行是概念序号 |
| `knowledge_point` | 不执行 | 知识点过关单，编号行是条目序号 |

### 回退机制

当 LLM 调用失败时，根据 `DocumentProfile.SegmentStrategy` 决定回退行为：

```csharp
private List<ParsedSegment> SplitSentencesWithFallbackAsync(
    string blockText,
    DocumentProfile? profile,
    CancellationToken cancellationToken)
{
    var strategy = profile?.SegmentStrategy ?? SegmentTypes.Sentence;

    // 仅 sentence 策略可回退到规则切割
    if (strategy == SegmentTypes.Sentence)
    {
        return SplitSentences(blockText);
    }

    // 其它策略不回退，抛异常触发任务级失败
    _logger.LogError(
        "LLM 分段失败且策略 {Strategy} 不支持规则回退，将触发任务失败",
        strategy);
    throw new InvalidOperationException(
        $"LLM segmentation failed for strategy '{strategy}', " +
        "rule-based fallback would produce a single oversized segment. " +
        "Please retry the document or check LLM service health.");
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
阶段二：LLM 智能分段（每个 chunk 1 次调用，受控并行 MaxConcurrency=2）
  │  输入：chunk.Text + DocumentProfile
  │  输出：List<SegmentResult>
  │  说明：SemaphoreSlim 限制并发，重叠推理模型"思考"时间
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

### 导入进度计算

#### 旧方案问题

旧方案使用固定阶段百分比，存在以下问题：

1. **parsing 阶段占 90%+ 时间但无中间进度**：从 45% 直接跳到 60%，用户看到长时间卡在 45%
2. **indexing 在 completed 之后执行**：进度从 100% 回退到 95%，不合理
3. **并行 chunk 处理时进度无法反映实际完成比例**：7 个 chunk 并行处理，但进度不更新
4. **不同文档大小差异大**：固定百分比无法适配

#### 新方案：基于阶段权重的动态进度

```
总进度 = Σ(各阶段权重 × 阶段内进度)
```

| 阶段 | 权重 | 阶段内进度 | 说明 |
|------|------|-----------|------|
| starting | 5% | 0→100% | 任务启动 |
| downloading | 5% | 0→100% | 下载文件 |
| analyzing | 10% | 0→100% | LLM 文档分析（1次调用） |
| parsing | 55% | 0→100% | LLM 分段（N个chunk），每完成1个chunk推进 1/N |
| writing_pages | 5% | 0→100% | 写入页面记录 |
| writing_segments | 10% | 0→100% | 写入分段+token |
| indexing | 10% | 0→100% | 写入搜索索引 |
| completed | — | 100% | 完成 |

**关键改进**：
- parsing 阶段（55%权重）按 chunk 完成数动态推进，不再卡住
- indexing 在 completed 之前执行，进度单调递增
- `ParseAsync` 接受 `IProgress<ParsingProgress>` 回调，`DocumentParserService` 在分析完成和每个 chunk 完成时报告进度
- `IngestionWorker` 通过回调将进度映射到 `UpdateJobProgressAsync`

**进度回调模型**：

```csharp
public record ParsingProgress
{
    public string Stage { get; init; }  // "analyzing" | "parsing"
    public int CurrentStep { get; init; }  // 当前步骤（0-based）
    public int TotalSteps { get; init; }   // 总步骤数
}
```

- analyzing 阶段：`CurrentStep=0, TotalSteps=1`，完成时 `CurrentStep=1`
- parsing 阶段：`TotalSteps=chunkCount`，每完成一个 chunk `CurrentStep++`

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
| LLM 调用延迟 | 单次调用耗时（含流式读取） | > 120s |
| SSE 流中断率 | 流式读取中途断开的比例 | > 5% |
| 回退率 | 回退到规则切割的比例 | > 10% |
| 分段质量 | 人工抽样评估 | - |

### 日志

```
[INFO] 文档分析完成：DocumentId={id}, Subject={subject}, DocType={docType}, Strategy={strategy}
[INFO] LLM 分段完成：DocumentId={id}, ChunkCount={count}, SegmentCount={segmentCount}
[INFO] LLM call completed in {ElapsedMs}ms (streaming), output length={Length}
[WARN] LLM 分段失败，回退到规则切割：DocumentId={id}, Error={error}
[WARN] LLM call attempt {Attempt}/{MaxRetries} failed after {ElapsedMs}ms, retrying
[ERROR] LLM 调用失败：DocumentId={id}, RetryCount={retryCount}, Error={error}
```

## 未来扩展

1. **多语言支持**：扩展 Prompt 支持更多语言
2. **自定义分段策略**：允许用户上传时指定分段策略
3. **分段质量反馈**：用户可以标记分段不准确的 segment，用于优化 Prompt
4. **批量重新入库**：支持批量对已有文档重新分段
5. **本地模型支持**：支持部署本地 LLM，降低 API 调用成本
