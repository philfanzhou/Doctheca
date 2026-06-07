using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;

namespace Ruoyu.Study.DocRetrieval.Service;

/// <summary>
/// 文档解析服务实现，支持 PDF、Word、PPT
/// </summary>
public partial class DocumentParserService : IDocumentParserService
{
    private readonly ILogger<DocumentParserService> _logger;

    // 缩写列表，用于句子边界识别时排除误判
    private static readonly HashSet<string> Abbreviations =
    [
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "vs", "etc", "e.g", "i.e", "U.S", "U.K"
    ];

    // 题号正则：行首数字 + 分隔符
    [GeneratedRegex(@"^\s*(\d+)\s*[.、．)\]】]", RegexOptions.Compiled)]
    private static partial Regex QuestionNumberRegex();

    // 选项正则：行首字母 + 分隔符
    [GeneratedRegex(@"^\s*([A-Da-d])\s*[.、．)\]】]", RegexOptions.Compiled)]
    private static partial Regex OptionRegex();

    public DocumentParserService(ILogger<DocumentParserService> logger)
    {
        _logger = logger;
    }

    public async Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default)
    {
        var sourceTypeLower = sourceType.ToLowerInvariant();

        return sourceTypeLower switch
        {
            "pdf" => await ParsePdfAsync(fileStream, cancellationToken),
            "docx" or "doc" => await ParseWordAsync(fileStream, cancellationToken),
            "pptx" or "ppt" => await ParsePptAsync(fileStream, cancellationToken),
            _ => throw new NotSupportedException($"不支持的文件类型：{sourceType}")
        };
    }

    #region PDF 解析

    private Task<ParsedDocument> ParsePdfAsync(Stream fileStream, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var document = PdfDocument.Open(fileStream);

        var pageNumber = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;

            var parsedPage = new ParsedPage { PageNumber = pageNumber };

            var pageText = page.Text;

            if (string.IsNullOrWhiteSpace(pageText))
            {
                _logger.LogWarning("PDF 第 {PageNumber} 页文本为空，可能需要 OCR 支持", pageNumber);
                result.Pages.Add(parsedPage);
                continue;
            }

            // OCR 后处理：合并连字符、移除多余空白、修正常见 OCR 错误
            pageText = OcrPostProcess(pageText);

            // 按换行分段，合并连续非空行为块（段落）
            var blocks = MergeTextIntoBlocks(pageText);

            // 对每个块做句子边界识别和题目边界识别
            var globalOffset = 0;
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                var blockText = blocks[blockIndex];
                var blockId = $"p{pageNumber}-b{blockIndex + 1}";

                // 句子切分
                var sentences = SplitSentences(blockText);
                for (var sentIndex = 0; sentIndex < sentences.Count; sentIndex++)
                {
                    var sentenceText = sentences[sentIndex];
                    parsedPage.Segments.Add(new ParsedSegment
                    {
                        BlockId = blockId,
                        SentenceId = $"{blockId}-s{sentIndex + 1}",
                        SegmentType = "sentence",
                        Text = sentenceText,
                        StartOffset = globalOffset,
                        EndOffset = globalOffset + sentenceText.Length,
                        Tokens = Tokenize(sentenceText)
                    });
                    globalOffset += sentenceText.Length;
                }

                // 题目边界识别
                var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
                parsedPage.Questions.AddRange(questions);
            }

            result.Pages.Add(parsedPage);
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// 将文本按换行分段，合并连续非空行为块（段落）
    /// </summary>
    private List<string> MergeTextIntoBlocks(string text)
    {
        var blocks = new List<string>();
        var currentBlock = new StringBuilder();

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var lineText = line.Trim();

            if (string.IsNullOrWhiteSpace(lineText))
            {
                // 空行视为段落分隔
                if (currentBlock.Length > 0)
                {
                    blocks.Add(currentBlock.ToString());
                    currentBlock.Clear();
                }
                continue;
            }

            if (currentBlock.Length > 0)
            {
                currentBlock.Append(' ');
            }
            currentBlock.Append(lineText);
        }

        if (currentBlock.Length > 0)
        {
            blocks.Add(currentBlock.ToString());
        }

        return blocks;
    }

    #endregion

    #region Word 解析

    private async Task<ParsedDocument> ParseWordAsync(Stream fileStream, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var doc = WordprocessingDocument.Open(fileStream, false);
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Word 文档缺少 MainDocumentPart");

        var body = mainPart.Document.Body
            ?? throw new InvalidOperationException("Word 文档缺少 Body");

        var paragraphs = body.Elements<Paragraph>().ToList();

        // Word 文档视为单页
        var parsedPage = new ParsedPage { PageNumber = 1 };
        var blocks = new List<string>();
        var currentBlock = new StringBuilder();

        foreach (var para in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var paraText = para.InnerText?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(paraText))
            {
                if (currentBlock.Length > 0)
                {
                    blocks.Add(currentBlock.ToString());
                    currentBlock.Clear();
                }
                continue;
            }

            if (currentBlock.Length > 0)
            {
                currentBlock.Append(' ');
            }
            currentBlock.Append(paraText);
        }

        if (currentBlock.Length > 0)
        {
            blocks.Add(currentBlock.ToString());
        }

        // 对每个块做句子边界识别和题目边界识别
        var globalOffset = 0;
        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var blockText = OcrPostProcess(blocks[blockIndex]);
            var blockId = $"p1-b{blockIndex + 1}";

            var sentences = SplitSentences(blockText);
            for (var sentIndex = 0; sentIndex < sentences.Count; sentIndex++)
            {
                var sentenceText = sentences[sentIndex];
                parsedPage.Segments.Add(new ParsedSegment
                {
                    BlockId = blockId,
                    SentenceId = $"{blockId}-s{sentIndex + 1}",
                    SegmentType = "sentence",
                    Text = sentenceText,
                    StartOffset = globalOffset,
                    EndOffset = globalOffset + sentenceText.Length,
                    Tokens = Tokenize(sentenceText)
                });
                globalOffset += sentenceText.Length;
            }

            var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
            parsedPage.Questions.AddRange(questions);
        }

        result.Pages.Add(parsedPage);
        return await Task.FromResult(result);
    }

    #endregion

    #region PPT 解析

    private async Task<ParsedDocument> ParsePptAsync(Stream fileStream, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var doc = PresentationDocument.Open(fileStream, false);
        var presentationPart = doc.PresentationPart
            ?? throw new InvalidOperationException("PPT 文档缺少 PresentationPart");

        var slideParts = presentationPart.SlideParts.ToList();

        for (var slideIndex = 0; slideIndex < slideParts.Count; slideIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slidePart = slideParts[slideIndex];
            var pageNumber = slideIndex + 1;
            var parsedPage = new ParsedPage { PageNumber = pageNumber };

            var texts = new List<string>();
            var shapes = slidePart.Slide.CommonSlideData?.ShapeTree?.Elements<DocumentFormat.OpenXml.Presentation.Shape>();
            if (shapes != null)
            {
                foreach (var shape in shapes)
                {
                    var textBody = shape.TextBody;
                    if (textBody != null)
                    {
                        var shapeText = string.Join(' ',
                            textBody.Elements<DocumentFormat.OpenXml.Drawing.Paragraph>()
                                .Select(p => p.InnerText?.Trim())
                                .Where(t => !string.IsNullOrWhiteSpace(t)));
                        if (!string.IsNullOrWhiteSpace(shapeText))
                        {
                            texts.Add(shapeText);
                        }
                    }
                }
            }

            if (texts.Count == 0)
            {
                _logger.LogWarning("PPT 第 {PageNumber} 页文本为空", pageNumber);
                result.Pages.Add(parsedPage);
                continue;
            }

            // 每个文本框视为一个块
            var globalOffset = 0;
            for (var blockIndex = 0; blockIndex < texts.Count; blockIndex++)
            {
                var blockText = OcrPostProcess(texts[blockIndex]);
                var blockId = $"p{pageNumber}-b{blockIndex + 1}";

                var sentences = SplitSentences(blockText);
                for (var sentIndex = 0; sentIndex < sentences.Count; sentIndex++)
                {
                    var sentenceText = sentences[sentIndex];
                    parsedPage.Segments.Add(new ParsedSegment
                    {
                        BlockId = blockId,
                        SentenceId = $"{blockId}-s{sentIndex + 1}",
                        SegmentType = "sentence",
                        Text = sentenceText,
                        StartOffset = globalOffset,
                        EndOffset = globalOffset + sentenceText.Length,
                        Tokens = Tokenize(sentenceText)
                    });
                    globalOffset += sentenceText.Length;
                }

                var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
                parsedPage.Questions.AddRange(questions);
            }

            result.Pages.Add(parsedPage);
        }

        return await Task.FromResult(result);
    }

    #endregion

    #region OCR 后处理

    /// <summary>
    /// OCR 后处理：合并连字符打断的单词、移除多余空白、修正常见 OCR 错误
    /// </summary>
    private static string OcrPostProcess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // 1. 合并连字符打断的单词：行尾 "word-\nword" → "wordword"
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(\w)-\s*\n\s*(\w)", "$1$2");

        // 2. 移除多余空白（多个空格/制表符 → 单个空格）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"[ \t]+", " ");

        // 3. 修正常见 OCR 错误（仅在大写字母前修正 0→O，在纯数字上下文不修正）
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Z])0", "O");   // A0 → AO
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"0(?=[A-Z])", "O");     // 0A → OA
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Za-z])1(?=[A-Za-z])", "l"); // l1l → lll (仅字母间)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Za-z])5(?=[A-Za-z])", "S"); // a5a → aSa (仅字母间)

        return text.Trim();
    }

    #endregion

    #region Token 分词与词干还原

    /// <summary>
    /// 对文本进行分词，生成 Token 列表（原始文本 + Porter 词干还原）
    /// </summary>
    private static List<ParsedToken> Tokenize(string text)
    {
        var tokens = new List<ParsedToken>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        // 使用正则按单词边界分词，只保留英文字母组成的单词
        var regex = new Regex(@"[a-zA-Z]+", RegexOptions.Compiled);
        foreach (Match match in regex.Matches(text))
        {
            var word = match.Value;
            var stem = PorterStem(word.ToLowerInvariant());
            tokens.Add(new ParsedToken
            {
                TokenText = word,
                TokenStem = stem,
                StartOffset = match.Index,
                EndOffset = match.Index + match.Length
            });
        }

        return tokens;
    }

    /// <summary>
    /// Porter Stemmer 算法精简实现（英语词干还原）
    /// </summary>
    private static string PorterStem(string word)
    {
        if (word.Length < 3)
            return word;

        // Step 1a: 复数和过去式
        if (word.EndsWith("sses")) word = word[..^2];
        else if (word.EndsWith("ies")) word = word[..^2];
        else if (word.EndsWith("ss")) { /* no change */ }
        else if (word.EndsWith("s")) word = word[..^1];

        // Step 1b: 进行时和过去式
        var step1bExtra = false;
        if (word.EndsWith("eed"))
        {
            if (Measure(word[..^3]) > 0) word = word[..^1];
        }
        else if (word.EndsWith("ed") && ContainsVowel(word[..^2]))
        {
            word = word[..^2];
            step1bExtra = true;
        }
        else if (word.EndsWith("ing") && ContainsVowel(word[..^3]))
        {
            word = word[..^3];
            step1bExtra = true;
        }

        if (step1bExtra)
        {
            if (word.EndsWith("at") || word.EndsWith("bl") || word.EndsWith("iz"))
                word += "e";
            else if (EndsWithDoubleConsonant(word) && !word.EndsWith("l") && !word.EndsWith("s") && !word.EndsWith("z"))
                word = word[..^1];
            else if (Measure(word) == 1 && EndsCVC(word))
                word += "e";
        }

        // Step 1c: y → i
        if (word.EndsWith("y") && ContainsVowel(word[..^1]))
            word = word[..^1] + "i";

        // Step 2: 常见后缀
        word = ReplaceSuffix(word, "ational", "ate");
        word = ReplaceSuffix(word, "tional", "tion");
        word = ReplaceSuffix(word, "enci", "ence");
        word = ReplaceSuffix(word, "anci", "ance");
        word = ReplaceSuffix(word, "izer", "ize");
        word = ReplaceSuffix(word, "abli", "able");
        word = ReplaceSuffix(word, "alli", "al");
        word = ReplaceSuffix(word, "entli", "ent");
        word = ReplaceSuffix(word, "eli", "e");
        word = ReplaceSuffix(word, "ousli", "ous");
        word = ReplaceSuffix(word, "ization", "ize");
        word = ReplaceSuffix(word, "ation", "ate");
        word = ReplaceSuffix(word, "ator", "ate");
        word = ReplaceSuffix(word, "alism", "al");
        word = ReplaceSuffix(word, "iveness", "ive");
        word = ReplaceSuffix(word, "fulness", "ful");
        word = ReplaceSuffix(word, "ousness", "ous");
        word = ReplaceSuffix(word, "aliti", "al");
        word = ReplaceSuffix(word, "iviti", "ive");
        word = ReplaceSuffix(word, "biliti", "ble");

        // Step 3: 更多后缀
        word = ReplaceSuffix(word, "icate", "ic");
        word = ReplaceSuffix(word, "ative", "");
        word = ReplaceSuffix(word, "alize", "al");
        word = ReplaceSuffix(word, "iciti", "ic");
        word = ReplaceSuffix(word, "ical", "ic");
        word = ReplaceSuffix(word, "ful", "");
        word = ReplaceSuffix(word, "ness", "");

        // Step 4: 去掉剩余后缀（m > 1）
        var step4Suffixes = new[] { "al", "ance", "ence", "er", "ic", "able", "ible", "ant", "ement", "ment", "ent", "ion", "ou", "ism", "ate", "iti", "ous", "ive", "ize" };
        foreach (var suffix in step4Suffixes)
        {
            if (word.EndsWith(suffix))
            {
                var stem = word[..^suffix.Length];
                if (suffix == "ion")
                {
                    if (Measure(stem) > 1 && stem.Length > 0 && (stem[^1] == 's' || stem[^1] == 't'))
                        word = stem;
                }
                else
                {
                    if (Measure(stem) > 1) word = stem;
                }
                break;
            }
        }

        // Step 5a: 去掉末尾 e
        if (word.EndsWith("e"))
        {
            var stem = word[..^1];
            if (Measure(stem) > 1 || (Measure(stem) == 1 && !EndsCVC(stem)))
                word = stem;
        }

        // Step 5b: ll → l (m > 1)
        if (word.EndsWith("ll") && Measure(word) > 1)
            word = word[..^1];

        return word;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static bool ContainsVowel(string word)
    {
        foreach (var c in word)
            if (IsVowel(c)) return true;
        return false;
    }

    /// <summary>计算 m 值（辅音-元音对数）</summary>
    private static int Measure(string word)
    {
        if (string.IsNullOrEmpty(word)) return 0;
        var i = 0;
        // 跳过开头的元音
        while (i < word.Length && IsVowel(word[i])) i++;
        var m = 0;
        while (i < word.Length)
        {
            // 辅音
            while (i < word.Length && !IsVowel(word[i])) i++;
            if (i >= word.Length) break;
            // 元音
            while (i < word.Length && IsVowel(word[i])) i++;
            m++;
        }
        return m;
    }

    private static bool EndsWithDoubleConsonant(string word)
    {
        if (word.Length < 2) return false;
        return word[^1] == word[^2] && !IsVowel(word[^1]);
    }

    /// <summary>是否以 辅音-元音-辅音 结尾（且最后一个辅音不是 w/x/y）</summary>
    private static bool EndsCVC(string word)
    {
        if (word.Length < 3) return false;
        return !IsVowel(word[^3]) && IsVowel(word[^2]) && !IsVowel(word[^1])
               && word[^1] is not ('w' or 'x' or 'y');
    }

    private static string ReplaceSuffix(string word, string suffix, string replacement)
    {
        if (!word.EndsWith(suffix)) return word;
        var stem = word[..^suffix.Length];
        return Measure(stem) > 0 ? stem + replacement : word;
    }

    #endregion

    #region 句子边界识别

    /// <summary>
    /// 句子边界识别：以 ". "/"! "/"? " 结尾视为句子结束，但排除缩写
    /// </summary>
    private List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return sentences;

        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            current.Append(text[i]);

            // 检查是否为句子结束位置
            if (IsSentenceBoundary(text, i))
            {
                var sentence = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }
                current.Clear();
            }
        }

        // 处理剩余文本
        var remaining = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            sentences.Add(remaining);
        }

        return sentences;
    }

    /// <summary>
    /// 判断当前位置是否为句子边界
    /// </summary>
    private bool IsSentenceBoundary(string text, int index)
    {
        var ch = text[index];

        // 只检查 . ! ? 后跟空格或文本结尾的情况
        if (ch != '.' && ch != '!' && ch != '?')
            return false;

        // 规则 3：引号内的句号不视为句子结束（"...text." 后面跟引号）
        if (ch == '.' && index < text.Length - 1)
        {
            var nextCh = text[index + 1];
            if (nextCh == '"' || nextCh == '\u201D' || nextCh == '\u201C' || nextCh == '\'' || nextCh == '\u2019')
            {
                // 句号在引号内，检查引号后是否有空格（引号结束+空格=句子结束）
                // 如果引号后没有空格或文本结尾，则不是句子边界
                var afterQuoteIdx = index + 2;
                if (afterQuoteIdx < text.Length && text[afterQuoteIdx] != ' ')
                    return false;
                // 引号后跟空格或文本结尾，这是句子边界（句号+引号结束句子）
            }
        }

        // 规则 4：数字中的句号不视为句子结束（3.14, 2026.06.01）
        if (ch == '.')
        {
            // 前面是数字
            if (index > 0 && char.IsDigit(text[index - 1]))
            {
                // 后面也是数字 → 小数点，不是句子边界
                if (index < text.Length - 1 && char.IsDigit(text[index + 1]))
                    return false;
            }
        }

        // 文本结尾
        if (index == text.Length - 1)
        {
            // 对于 '.' 需要排除缩写
            if (ch == '.' && IsAbbreviation(text, index))
                return false;
            return true;
        }

        // 后面必须跟空格才算句子结束
        if (text[index + 1] != ' ')
            return false;

        // 对于 '.' 需要排除缩写
        if (ch == '.' && IsAbbreviation(text, index))
            return false;

        return true;
    }

    /// <summary>
    /// 检查当前位置的 '.' 是否属于缩写
    /// </summary>
    private bool IsAbbreviation(string text, int dotIndex)
    {
        // 向前查找缩写词，最多取 dotIndex 前 5 个字符
        var start = Math.Max(0, dotIndex - 5);
        var beforeDot = text.Substring(start, dotIndex - start);

        foreach (var abbr in Abbreviations)
        {
            if (beforeDot.EndsWith(abbr, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    #endregion

    #region 题目边界识别

    /// <summary>
    /// 从文本中提取题目和选项
    /// </summary>
    private List<ParsedQuestion> ExtractQuestions(string text, string blockId, ref int globalOffset)
    {
        var questions = new List<ParsedQuestion>();
        var lines = text.Split('\n');

        string? currentQuestionId = null;
        var stemBuilder = new StringBuilder();
        var options = new Dictionary<string, string>();
        var questionStartOffset = globalOffset;
        var lineOffset = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            var questionMatch = QuestionNumberRegex().Match(trimmedLine);
            var optionMatch = OptionRegex().Match(trimmedLine);

            if (questionMatch.Success)
            {
                // 保存上一道题
                if (currentQuestionId != null)
                {
                    questions.Add(BuildParsedQuestion(
                        currentQuestionId, stemBuilder, options,
                        questionStartOffset, lineOffset));
                }

                // 开始新题
                currentQuestionId = $"q{questionMatch.Groups[1].Value}";
                stemBuilder.Clear();
                stemBuilder.Append(trimmedLine);
                options.Clear();
                questionStartOffset = globalOffset + lineOffset;
            }
            else if (optionMatch.Success && currentQuestionId != null)
            {
                // 选项
                var optionLetter = optionMatch.Groups[1].Value.ToUpperInvariant();
                options[optionLetter] = trimmedLine;
                stemBuilder.Append(' ').Append(trimmedLine);
            }
            else if (currentQuestionId != null)
            {
                // 题目续行
                stemBuilder.Append(' ').Append(trimmedLine);
            }

            lineOffset += line.Length + 1; // +1 for \n
        }

        // 保存最后一道题
        if (currentQuestionId != null)
        {
            questions.Add(BuildParsedQuestion(
                currentQuestionId, stemBuilder, options,
                questionStartOffset, lineOffset));
        }

        return questions;
    }

    private ParsedQuestion BuildParsedQuestion(
        string questionId, StringBuilder stemBuilder,
        Dictionary<string, string> options,
        int startOffset, int endOffset)
    {
        var stem = stemBuilder.ToString().Trim();
        string? optionsJson = null;
        if (options.Count > 0)
        {
            optionsJson = JsonSerializer.Serialize(options);
        }

        return new ParsedQuestion
        {
            QuestionId = questionId,
            Stem = stem,
            OptionsJson = optionsJson,
            AnswerArea = null,
            StartOffset = startOffset,
            EndOffset = endOffset
        };
    }

    #endregion
}
