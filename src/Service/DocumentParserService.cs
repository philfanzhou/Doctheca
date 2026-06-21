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
/// Document parsing service implementation, supports PDF, Word, PPT.
/// Uses LLM for intelligent segmentation when available, falls back to rule-based splitting.
/// </summary>
public partial class DocumentParserService : IDocumentParserService
{
    private readonly ILogger<DocumentParserService> _logger;
    private readonly ILlmSegmentationService? _llmSegmentation;

    // Abbreviation list, used to exclude false positives in sentence boundary detection
    private static readonly HashSet<string> Abbreviations =
    [
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "vs", "etc", "e.g", "i.e", "U.S", "U.K"
    ];

    // Question number regex: leading digit + separator
    [GeneratedRegex(@"^\s*(\d+)\s*[.、．)\]】]", RegexOptions.Compiled)]
    private static partial Regex QuestionNumberRegex();

    // Option regex: leading letter + separator
    [GeneratedRegex(@"^\s*([A-Da-d])\s*[.、．)\]】]", RegexOptions.Compiled)]
    private static partial Regex OptionRegex();

    #region Chunking Infrastructure

    /// <summary>
    /// A text chunk with offset-to-page mapping for LLM segmentation.
    /// </summary>
    internal record TextChunk
    {
        public string Text { get; init; } = string.Empty;
        public int GlobalStartOffset { get; init; }
        public List<PageRange> PageRanges { get; init; } = [];
    }

    /// <summary>
    /// Maps a character range within a chunk to a page number.
    /// </summary>
    internal record PageRange
    {
        public int ChunkStartOffset { get; init; }
        public int ChunkEndOffset { get; init; }
        public int PageNumber { get; init; }
    }

    /// <summary>
    /// Concatenate page texts and split into chunks by capacity (ChunkSize).
    /// Prefers splitting at paragraph boundaries (\n\n) to preserve semantic coherence.
    /// </summary>
    internal static List<TextChunk> ChunkByCapacity(
        List<(int PageNumber, string Text)> pages, int chunkSize)
    {
        if (pages.Count == 0) return [];

        // Build the full text with page separator tracking
        var fullText = new StringBuilder();
        var pageRanges = new List<(int GlobalStart, int GlobalEnd, int PageNumber)>();
        var separator = "\n\n";

        for (var i = 0; i < pages.Count; i++)
        {
            var (pageNumber, text) = pages[i];
            if (string.IsNullOrWhiteSpace(text)) continue;

            var globalStart = fullText.Length;
            if (fullText.Length > 0)
            {
                fullText.Append(separator);
            }
            fullText.Append(text);
            var globalEnd = fullText.Length;

            pageRanges.Add((globalStart, globalEnd, pageNumber));
        }

        if (fullText.Length == 0) return [];

        var textStr = fullText.ToString();
        var chunks = new List<TextChunk>();
        var pos = 0;

        while (pos < textStr.Length)
        {
            var remaining = textStr.Length - pos;
            if (remaining <= chunkSize)
            {
                // Last chunk
                chunks.Add(CreateChunk(textStr[pos..], pos, pageRanges));
                break;
            }

            // Find the best split point within chunkSize
            var splitAt = FindSplitPoint(textStr, pos, chunkSize);
            chunks.Add(CreateChunk(textStr[pos..splitAt], pos, pageRanges));
            pos = splitAt;
        }

        return chunks;
    }

    /// <summary>
    /// Find the best split point within the text, preferring paragraph boundaries.
    /// </summary>
    private static int FindSplitPoint(string text, int start, int maxLength)
    {
        var end = Math.Min(start + maxLength, text.Length);
        if (end >= text.Length) return text.Length;

        // Look for paragraph boundary (\n\n) near the end, searching backward
        var searchLen = Math.Min(maxLength / 5, end - start); // search within 20% of chunk size
        var searchFrom = end - 1;

        // Try paragraph boundary (\n\n)
        if (searchLen >= 2)
        {
            var paraBreak = text.LastIndexOf("\n\n", searchFrom, searchLen);
            if (paraBreak > start)
            {
                return paraBreak + 2;
            }
        }

        // Fallback: single newline
        var newline = text.LastIndexOf('\n', searchFrom, searchLen);
        if (newline > start)
        {
            return newline + 1;
        }

        // Last fallback: space
        var space = text.LastIndexOf(' ', searchFrom, searchLen);
        if (space > start)
        {
            return space + 1;
        }

        // Hard split at max length
        return end;
    }

    /// <summary>
    /// Create a TextChunk from a substring with page range mapping.
    /// </summary>
    private static TextChunk CreateChunk(string text, int globalStart,
        List<(int GlobalStart, int GlobalEnd, int PageNumber)> pageRanges)
    {
        var globalEnd = globalStart + text.Length;
        var ranges = new List<PageRange>();

        foreach (var (pStart, pEnd, pNum) in pageRanges)
        {
            // Check overlap between chunk range and page range
            var overlapStart = Math.Max(globalStart, pStart);
            var overlapEnd = Math.Min(globalEnd, pEnd);
            if (overlapStart < overlapEnd)
            {
                ranges.Add(new PageRange
                {
                    ChunkStartOffset = overlapStart - globalStart,
                    ChunkEndOffset = overlapEnd - globalStart,
                    PageNumber = pNum
                });
            }
        }

        return new TextChunk
        {
            Text = text,
            GlobalStartOffset = globalStart,
            PageRanges = ranges
        };
    }

    /// <summary>
    /// Map a chunk-local offset to the corresponding page number.
    /// If the offset spans pages, returns the page where the offset starts.
    /// </summary>
    internal static int MapOffsetToPage(List<PageRange> pageRanges, int chunkOffset)
    {
        foreach (var range in pageRanges)
        {
            if (chunkOffset >= range.ChunkStartOffset && chunkOffset < range.ChunkEndOffset)
            {
                return range.PageNumber;
            }
        }

        // Fallback: return the last page range's number
        return pageRanges.Count > 0 ? pageRanges[^1].PageNumber : 1;
    }

    #endregion

    public DocumentParserService(
        ILogger<DocumentParserService> logger,
        ILlmSegmentationService? llmSegmentation = null)
    {
        _logger = logger;
        _llmSegmentation = llmSegmentation;
    }

    public async Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, IProgress<ParsingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var sourceTypeLower = sourceType.ToLowerInvariant();

        return sourceTypeLower switch
        {
            SourceTypes.Pdf => await ParsePdfAsync(fileStream, progress, cancellationToken),
            SourceTypes.Word => await ParseWordAsync(fileStream, progress, cancellationToken),
            SourceTypes.Ppt => await ParsePptAsync(fileStream, progress, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported file type: {sourceType}")
        };
    }

    #region PDF Parsing

    private async Task<ParsedDocument> ParsePdfAsync(Stream fileStream, IProgress<ParsingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        // First pass: extract all page texts
        var pageTexts = new List<(int PageNumber, string Text)>();
        using var document = PdfDocument.Open(fileStream);

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = pageTexts.Count + 1;
            var pageText = page.Text;

            if (string.IsNullOrWhiteSpace(pageText))
            {
                _logger.LogWarning("PDF page {PageNumber} has no text, OCR support may be needed", pageNumber);
                pageTexts.Add((pageNumber, string.Empty));
                continue;
            }

            pageTexts.Add((pageNumber, OcrPostProcess(pageText)));
        }

        // LLM analysis: determine document profile from first non-empty pages
        DocumentProfile? profile = null;
        if (_llmSegmentation != null)
        {
            profile = await AnalyzeDocumentAsync(pageTexts, cancellationToken);
            progress?.Report(new ParsingProgress { Stage = "analyzing", CompletedSteps = 1, TotalSteps = 1 });
        }

        result.Profile = profile;

        // Segment using capacity-based chunking
        await SegmentPagesAsync(pageTexts, profile, result, progress, cancellationToken);

        return result;
    }

    #endregion

    #region Word Parsing

    private async Task<ParsedDocument> ParseWordAsync(Stream fileStream, IProgress<ParsingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var doc = WordprocessingDocument.Open(fileStream, false);
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Word document missing MainDocumentPart");

        var body = mainPart.Document.Body
            ?? throw new InvalidOperationException("Word document missing Body");

        var paragraphs = body.Elements<Paragraph>().ToList();

        // Detect page breaks from explicit markers
        var pageBreakIndices = DetectPageBreaks(doc, paragraphs);
        if (pageBreakIndices.Count > 0)
        {
            _logger.LogInformation("Word document: detected {Count} page break(s)", pageBreakIndices.Count);
        }
        else
        {
            _logger.LogWarning("Word document: no page break markers detected, treating as single page");
        }

        // Split paragraphs into pages based on page break markers
        var pages = new List<(int PageNumber, List<Paragraph> Paragraphs)>();
        var currentPageParagraphs = new List<Paragraph>();
        var pageNumber = 1;

        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (pageBreakIndices.Contains(i) && currentPageParagraphs.Count > 0)
            {
                pages.Add((pageNumber++, currentPageParagraphs));
                currentPageParagraphs = new List<Paragraph>();
            }
            currentPageParagraphs.Add(paragraphs[i]);
        }

        if (currentPageParagraphs.Count > 0)
        {
            pages.Add((pageNumber, currentPageParagraphs));
        }

        // LLM analysis from first page's content
        DocumentProfile? profile = null;
        if (_llmSegmentation != null && pages.Count > 0)
        {
            var previewBuilder = new StringBuilder();
            foreach (var (_, paras) in pages)
            {
                foreach (var para in paras)
                {
                    var text = para.InnerText?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (previewBuilder.Length > 0) previewBuilder.Append("\n\n");
                        previewBuilder.Append(text);
                    }
                    if (previewBuilder.Length >= 2000) break;
                }
                if (previewBuilder.Length >= 2000) break;
            }

            if (previewBuilder.Length > 0)
            {
                profile = await AnalyzeDocumentAsync([(1, previewBuilder.ToString())], cancellationToken);
                progress?.Report(new ParsingProgress { Stage = "analyzing", CompletedSteps = 1, TotalSteps = 1 });
            }
        }

        result.Profile = profile;

        // Convert pages to text list and segment
        var pageTexts = pages
            .Select(p => (p.PageNumber, string.Join("\n\n",
                MergeParagraphsIntoBlocks(p.Paragraphs).Select(b => OcrPostProcess(b)))))
            .ToList();

        await SegmentPagesAsync(pageTexts, profile, result, progress, cancellationToken);

        return result;
    }

    /// <summary>
    /// Merge paragraphs into text blocks (consecutive non-empty paragraphs → one block).
    /// </summary>
    private static List<string> MergeParagraphsIntoBlocks(List<Paragraph> paragraphs)
    {
        var blocks = new List<string>();
        var currentBlock = new StringBuilder();

        foreach (var para in paragraphs)
        {
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

        return blocks;
    }

    /// <summary>
    /// Detect page breaks in a Word document by checking explicit markers.
    /// Returns the set of paragraph indices where a new page begins.
    /// Detection priority: explicit page break > pageBreakBefore > style-based > section break.
    /// </summary>
    private HashSet<int> DetectPageBreaks(WordprocessingDocument doc, List<Paragraph> paragraphs)
    {
        var result = new HashSet<int>();

        // Build set of style IDs that have PageBreakBefore
        var pbbStyleIds = new HashSet<string>();
        var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
        if (stylesPart?.Styles != null)
        {
            foreach (var style in stylesPart.Styles.Descendants<Style>())
            {
                if (style.StyleId?.Value == null) continue;
                var pbb = style.StyleParagraphProperties?.GetFirstChild<PageBreakBefore>();
                if (pbb != null && (pbb.Val == null || pbb.Val.Value))
                {
                    pbbStyleIds.Add(style.StyleId.Value);
                }
            }
        }

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var para = paragraphs[i];

            // 1. Explicit page break in a run (w:br type="page")
            if (para.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page))
            {
                result.Add(i);
                continue;
            }

            // 2. PageBreakBefore property on the paragraph
            var pbb = para.ParagraphProperties?.GetFirstChild<PageBreakBefore>();
            if (pbb != null && (pbb.Val == null || pbb.Val.Value))
            {
                result.Add(i);
                continue;
            }

            // 3. Paragraph style has PageBreakBefore
            var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (styleId != null && pbbStyleIds.Contains(styleId))
            {
                result.Add(i);
                continue;
            }

            // 4. Inline section break (NextPage / EvenPage / OddPage)
            var sectPr = para.ParagraphProperties?.GetFirstChild<SectionProperties>();
            if (sectPr != null)
            {
                var mark = sectPr.GetFirstChild<SectionType>()?.Val?.Value
                           ?? SectionMarkValues.NextPage; // default is nextPage
                if (mark != SectionMarkValues.Continuous)
                {
                    result.Add(i);
                }
            }
        }

        return result;
    }

    #endregion

    #region PPT Parsing

    private async Task<ParsedDocument> ParsePptAsync(Stream fileStream, IProgress<ParsingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var doc = PresentationDocument.Open(fileStream, false);
        var presentationPart = doc.PresentationPart
            ?? throw new InvalidOperationException("PPT document missing PresentationPart");

        var slideParts = presentationPart.SlideParts.ToList();

        // First pass: extract all slide texts
        var slideTexts = new List<(int PageNumber, List<string> Blocks)>();
        foreach (var slidePart in slideParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = slideTexts.Count + 1;

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
                _logger.LogWarning("PPT page {PageNumber} has no text", pageNumber);
            }

            slideTexts.Add((pageNumber, texts));
        }

        // LLM analysis
        DocumentProfile? profile = null;
        if (_llmSegmentation != null)
        {
            var preview = string.Join("\n\n", slideTexts
                .Where(s => s.Blocks.Count > 0)
                .Take(3)
                .SelectMany(s => s.Blocks));
            if (preview.Length > 0)
            {
                profile = await AnalyzeDocumentAsync([(1, preview)], cancellationToken);
                progress?.Report(new ParsingProgress { Stage = "analyzing", CompletedSteps = 1, TotalSteps = 1 });
            }
        }

        result.Profile = profile;

        // Convert slides to text list and segment
        var pageTexts = slideTexts
            .Select(s => (s.PageNumber, string.Join("\n\n",
                s.Blocks.Select(b => OcrPostProcess(b)))))
            .ToList();

        await SegmentPagesAsync(pageTexts, profile, result, progress, cancellationToken);

        return result;
    }

    #endregion

    #region LLM Segmentation Helpers

    /// <summary>
    /// Analyze document using LLM to determine subject, type, and segmentation strategy.
    /// Falls back to default profile on failure.
    /// </summary>
    private async Task<DocumentProfile> AnalyzeDocumentAsync(
        List<(int PageNumber, string Text)> pageTexts,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build preview from first non-empty pages (up to 2000 chars)
            var previewBuilder = new StringBuilder();
            foreach (var (_, text) in pageTexts)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (previewBuilder.Length > 0) previewBuilder.Append("\n\n");
                previewBuilder.Append(text);
                if (previewBuilder.Length >= 2000) break;
            }

            var preview = previewBuilder.ToString();
            if (preview.Length > 2000)
                preview = preview[..2000];

            var profile = await _llmSegmentation!.AnalyzeDocumentAsync(preview, cancellationToken);
            _logger.LogInformation("Document analysis completed: Subject={Subject}, DocType={DocType}, Strategy={Strategy}",
                profile.Subject, profile.DocType, profile.SegmentStrategy);
            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM document analysis failed, using default profile");
            return new DocumentProfile();
        }
    }

    /// <summary>
    /// Segment pages using capacity-based chunking + LLM.
    /// Shared by all file format parsers. Chunks text by ChunkSize, calls LLM per chunk,
    /// and maps segments back to their source pages.
    /// </summary>
    private async Task SegmentPagesAsync(
        List<(int PageNumber, string Text)> pageTexts,
        DocumentProfile? profile,
        ParsedDocument result,
        IProgress<ParsingProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Filter out empty pages but track them for the result
        var nonEmptyPages = pageTexts.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();

        // Initialize all pages in result (including empty ones)
        foreach (var (pageNumber, _) in pageTexts)
        {
            result.Pages.Add(new ParsedPage { PageNumber = pageNumber });
        }

        if (nonEmptyPages.Count == 0) return;

        // Determine chunk size — if LLM is not available (ChunkSize <= 0), use rule-based fallback
        var llmAvailable = _llmSegmentation != null && profile != null && _llmSegmentation.ChunkSize > 0;
        var chunkSize = llmAvailable ? _llmSegmentation!.ChunkSize : 1500;

        // 早检查：非 sentence 策略必须由 LLM 切分，规则切割会产生整段单条记录污染搜索结果
        if (profile != null && profile.SegmentStrategy != SegmentTypes.Sentence && !llmAvailable)
        {
            throw new InvalidOperationException(
                $"Cannot segment document with strategy '{profile.SegmentStrategy}' " +
                "without LLM service. Please configure LLM segmentation in appsettings.json.");
        }

        var chunks = ChunkByCapacity(nonEmptyPages, chunkSize);

        _logger.LogInformation("Segmenting {PageCount} pages in {ChunkCount} chunks (ChunkSize={ChunkSize})",
            nonEmptyPages.Count, chunks.Count, chunkSize);

        // Build page lookup for adding segments
        var pageLookup = result.Pages.ToDictionary(p => p.PageNumber, p => p);

        // Track segment index per page for SentenceId
        var segmentIndexPerPage = new Dictionary<int, int>();

        // Process chunks in parallel for faster LLM calls
        var chunkResults = await ProcessChunksAsync(chunks, llmAvailable, profile, progress, cancellationToken);

        foreach (var (chunk, segments, llmFailed) in chunkResults)
        {
            // LLM 配过但本 chunk 失败，且当前策略不允许规则回退：
            // 抛异常让 IngestionWorker 走 FailIngestionJobAsync 路径，
            // 避免产生整段单条记录污染搜索结果
            if (llmFailed && profile != null && profile.SegmentStrategy != SegmentTypes.Sentence)
            {
                throw new InvalidOperationException(
                    $"LLM segmentation failed for chunk at global offset {chunk.GlobalStartOffset} " +
                    $"with strategy '{profile.SegmentStrategy}', and rule-based fallback is unsuitable. " +
                    "Please retry the document or check LLM service health.");
            }

            // Map segments back to pages
            foreach (var seg in segments)
            {
                // 防御性过滤：LLM 偶发返回"摘要/标题"型整块 segment（text 接近 chunk.Text 长度），
                // 与正常词条混在一起入库会污染搜索结果。直接丢弃。
                // 关键条件：必须同时有多个 segment（孤立的 1 个 segment 可能是合法输出，比如短文本）。
                // 阈值用 0.9 而非 1.0 是为了容忍 LLM 偶尔追加空格/换行的边界情况。
                if (segments.Count > 1 && seg.Text.Length >= chunk.Text.Length * 0.9)
                {
                    _logger.LogWarning(
                        "Discarding suspiciously large segment ({Len} chars vs chunk {ChunkLen}), " +
                        "likely a LLM summary/header artifact. ChunkOffset={GlobalStartOffset}",
                        seg.Text.Length, chunk.Text.Length, chunk.GlobalStartOffset);
                    continue;
                }

                var pageNumber = MapOffsetToPage(chunk.PageRanges, seg.StartOffset);
                if (!pageLookup.TryGetValue(pageNumber, out var parsedPage)) continue;

                var segIndex = segmentIndexPerPage.GetValueOrDefault(pageNumber, 0) + 1;
                segmentIndexPerPage[pageNumber] = segIndex;

                parsedPage.Segments.Add(new ParsedSegment
                {
                    SentenceId = $"p{pageNumber}-s{segIndex}",
                    SegmentType = seg.SegmentType,
                    Text = seg.Text,
                    StartOffset = seg.StartOffset,
                    EndOffset = seg.EndOffset,
                    Tokens = Tokenize(seg.Text)
                });
            }
        }

        // Extract questions per page — only for question strategy
        // Other strategies (word_entry/concept/knowledge_point/sentence) have numbered items
        // that are not exam questions; running ExtractQuestions would misidentify them and
        // produce oversized question_segments records that pollute search results.
        if (profile?.SegmentStrategy == SegmentTypes.Question)
        {
            foreach (var (pageNumber, text) in nonEmptyPages)
            {
                if (!pageLookup.TryGetValue(pageNumber, out var parsedPage)) continue;
                var questions = ExtractQuestions(text, pageNumber);
                parsedPage.Questions.AddRange(questions);
            }
        }
    }

    private List<SegmentWithOffset> FallbackSegment(string text, DocumentProfile? profile)
    {
        var strategy = profile?.SegmentStrategy ?? SegmentTypes.Sentence;

        // 仅 sentence 策略允许回退到 SplitSentences：教材/阅读材料含句末标点，
        // 规则切割能产出有效分段。其它策略（word_entry/concept/question/knowledge_point）
        // 强制走规则切割会产生整段单条记录，污染搜索结果。
        if (strategy == SegmentTypes.Sentence)
        {
            return SplitSentences(text).Select(t => new SegmentWithOffset
            {
                Text = t,
                StartOffset = 0,
                EndOffset = t.Length,
                SegmentType = SegmentTypes.Sentence
            }).ToList();
        }

        _logger.LogError(
            "LLM 分段失败且策略 {Strategy} 不支持规则回退，将触发任务级失败",
            strategy);
        return new List<SegmentWithOffset>();
    }

    /// <summary>
    /// Process chunks with controlled parallelism, returning segments for each chunk.
    /// Returns whether each chunk used the fallback path (LLM was configured but failed),
    /// so the caller can decide whether to fail the document.
    ///
    /// Concurrency is controlled by ILlmSegmentationService.MaxConcurrency:
    /// - 1 = fully serial (safest for rate-limited providers)
    /// - 2-3 = controlled parallelism (overlaps "thinking" time of reasoning models)
    /// </summary>
    private async Task<List<(TextChunk Chunk, List<SegmentWithOffset> Segments, bool LlmFailed)>> ProcessChunksAsync(
        List<TextChunk> chunks, bool llmAvailable, DocumentProfile? profile, IProgress<ParsingProgress>? progress, CancellationToken cancellationToken)
    {
        var maxConcurrency = llmAvailable ? _llmSegmentation!.MaxConcurrency : 1;
        var results = new (TextChunk Chunk, List<SegmentWithOffset> Segments, bool LlmFailed)[chunks.Count];
        var completedChunks = 0;

        if (maxConcurrency <= 1)
        {
            // Serial path — simple loop, preserves order
            for (var i = 0; i < chunks.Count; i++)
            {
                results[i] = await ProcessChunkAsync(chunks[i], i, chunks.Count, llmAvailable, profile, cancellationToken);
                completedChunks++;
                progress?.Report(new ParsingProgress { Stage = "parsing", CompletedSteps = completedChunks, TotalSteps = chunks.Count });
            }
        }
        else
        {
            // Parallel path with semaphore to limit concurrency
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new Task<(int Index, TextChunk Chunk, List<SegmentWithOffset> Segments, bool LlmFailed)>[chunks.Count];

            for (var i = 0; i < chunks.Count; i++)
            {
                var index = i;
                tasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var result = await ProcessChunkAsync(chunks[index], index, chunks.Count, llmAvailable, profile, cancellationToken);
                        // Thread-safe increment and progress report
                        var completed = Interlocked.Increment(ref completedChunks);
                        progress?.Report(new ParsingProgress { Stage = "parsing", CompletedSteps = completed, TotalSteps = chunks.Count });
                        return (index, result.Chunk, result.Segments, result.LlmFailed);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);
            }

            var taskResults = await Task.WhenAll(tasks);
            foreach (var r in taskResults)
            {
                results[r.Index] = (r.Chunk, r.Segments, r.LlmFailed);
            }
        }

        return results.ToList();
    }

    private async Task<(TextChunk Chunk, List<SegmentWithOffset> Segments, bool LlmFailed)> ProcessChunkAsync(
        TextChunk chunk, int index, int totalChunks, bool llmAvailable, DocumentProfile? profile, CancellationToken cancellationToken)
    {
        List<SegmentWithOffset> segments;
        var llmFailed = false;

        if (llmAvailable && profile != null)
        {
            try
            {
                _logger.LogInformation(
                    "Processing chunk {ChunkIndex}/{ChunkCount} at offset {GlobalStartOffset}",
                    index + 1, totalChunks, chunk.GlobalStartOffset);

                var llmSegments = await _llmSegmentation!.SegmentTextAsync(chunk.Text, profile, cancellationToken);
                if (llmSegments.Count > 0)
                {
                    segments = llmSegments.Select(s => new SegmentWithOffset
                    {
                        Text = s.Text, StartOffset = s.StartOffset,
                        EndOffset = s.EndOffset, SegmentType = s.SegmentType
                    }).ToList();
                }
                else
                {
                    _logger.LogWarning(
                        "LLM 返回空 segments，回退到规则切割：ChunkOffset={GlobalStartOffset}",
                        chunk.GlobalStartOffset);
                    segments = FallbackSegment(chunk.Text, profile);
                    llmFailed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LLM 分段失败，回退到规则切割：ChunkOffset={GlobalStartOffset}",
                    chunk.GlobalStartOffset);
                segments = FallbackSegment(chunk.Text, profile);
                llmFailed = true;
            }
        }
        else
        {
            // LLM 未配置：fallback 是默认行为，不标记为失败
            segments = FallbackSegment(chunk.Text, profile);
        }

        return (chunk, segments, llmFailed);
    }

    /// <summary>
    /// Internal record for segment with offset information
    /// </summary>
    private record SegmentWithOffset
    {
        public string Text { get; init; } = string.Empty;
        public int StartOffset { get; init; }
        public int EndOffset { get; init; }
        public string SegmentType { get; init; } = SegmentTypes.Sentence;
    }

    #endregion

    #region OCR Post-processing

    /// <summary>
    /// OCR post-processing: merge hyphen-broken words, remove extra whitespace, fix common OCR errors
    /// </summary>
    private static string OcrPostProcess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // 1. Merge hyphen-broken words: line-end "word-\nword" → "wordword"
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(\w)-\s*\n\s*(\w)", "$1$2");

        // 2. Remove extra whitespace (multiple spaces/tabs → single space)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"[ \t]+", " ");

        // 3. Fix common OCR errors (only correct 0→O before uppercase letters, not in pure digit context)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Z])0", "O");   // A0 → AO
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"0(?=[A-Z])", "O");     // 0A → OA
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Za-z])1(?=[A-Za-z])", "l"); // l1l → lll (between letters only)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[A-Za-z])5(?=[A-Za-z])", "S"); // a5a → aSa (between letters only)

        return text.Trim();
    }

    #endregion

    #region Token Tokenization and Stemming

    /// <summary>
    /// Tokenize text, generate token list (original text + Porter stemming)
    /// </summary>
    private static List<ParsedToken> Tokenize(string text)
    {
        var tokens = new List<ParsedToken>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        // Tokenize by word boundary using regex, keep only English letter words
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
    /// Porter Stemmer algorithm simplified implementation (English stemming)
    /// </summary>
    private static string PorterStem(string word)
    {
        if (word.Length < 3)
            return word;

        // Step 1a: Plurals and past tense
        if (word.EndsWith("sses")) word = word[..^2];
        else if (word.EndsWith("ies")) word = word[..^2];
        else if (word.EndsWith("ss")) { /* no change */ }
        else if (word.EndsWith("s")) word = word[..^1];

        // Step 1b: Progressive and past tense
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

        // Step 2: Common suffixes
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

        // Step 3: More suffixes
        word = ReplaceSuffix(word, "icate", "ic");
        word = ReplaceSuffix(word, "ative", "");
        word = ReplaceSuffix(word, "alize", "al");
        word = ReplaceSuffix(word, "iciti", "ic");
        word = ReplaceSuffix(word, "ical", "ic");
        word = ReplaceSuffix(word, "ful", "");
        word = ReplaceSuffix(word, "ness", "");

        // Step 4: Remove remaining suffixes (m > 1)
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

        // Step 5a: Remove trailing e
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

    /// <summary>Calculate m value (consonant-vowel pair count)</summary>
    private static int Measure(string word)
    {
        if (string.IsNullOrEmpty(word)) return 0;
        var i = 0;
        // Skip leading vowels
        while (i < word.Length && IsVowel(word[i])) i++;
        var m = 0;
        while (i < word.Length)
        {
            // Consonant
            while (i < word.Length && !IsVowel(word[i])) i++;
            if (i >= word.Length) break;
            // Vowel
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

    /// <summary>Whether ending with consonant-vowel-consonant (and last consonant is not w/x/y)</summary>
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

    #region Sentence Boundary Detection

    /// <summary>
    /// Sentence boundary detection: treat ". "/"! "/"? " endings as sentence boundaries, excluding abbreviations
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

            // Check if this is a sentence boundary
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

        // Process remaining text
        var remaining = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            sentences.Add(remaining);
        }

        return sentences;
    }

    /// <summary>
    /// Determine if current position is a sentence boundary
    /// </summary>
    private bool IsSentenceBoundary(string text, int index)
    {
        var ch = text[index];

        // Only check . ! ? followed by space or end of text
        if (ch != '.' && ch != '!' && ch != '?')
            return false;

        // Rule 3: Period inside quotes not treated as sentence end ("...text." followed by quote)
        if (ch == '.' && index < text.Length - 1)
        {
            var nextCh = text[index + 1];
            if (nextCh == '"' || nextCh == '\u201D' || nextCh == '\u201C' || nextCh == '\'' || nextCh == '\u2019')
            {
                // Period inside quotes, check if space follows quote (quote end + space = sentence boundary)
                // If no space or text end after quote, not a sentence boundary
                var afterQuoteIdx = index + 2;
                if (afterQuoteIdx < text.Length && text[afterQuoteIdx] != ' ')
                    return false;
                // Space or text end after quote, this is a sentence boundary (period + quote ends sentence)
            }
        }

        // Rule 4: Period in numbers not treated as sentence end (3.14, 2026.06.01)
        if (ch == '.')
        {
            // Preceded by digit
            if (index > 0 && char.IsDigit(text[index - 1]))
            {
                // Followed by digit → decimal point, not a sentence boundary
                if (index < text.Length - 1 && char.IsDigit(text[index + 1]))
                    return false;
            }
        }

        // End of text
        if (index == text.Length - 1)
        {
            // For '.' need to exclude abbreviations
            if (ch == '.' && IsAbbreviation(text, index))
                return false;
            return true;
        }

        // Must be followed by space to be sentence end
        if (text[index + 1] != ' ')
            return false;

        // For '.' need to exclude abbreviations
        if (ch == '.' && IsAbbreviation(text, index))
            return false;

        return true;
    }

    /// <summary>
    /// Check if the '.' at current position belongs to an abbreviation
    /// </summary>
    private bool IsAbbreviation(string text, int dotIndex)
    {
        // Look back for abbreviation, take at most 5 characters before dotIndex
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

    #region Question Boundary Detection

    /// <summary>
    /// Extract questions and options from text (page-scoped).
    /// </summary>
    private List<ParsedQuestion> ExtractQuestions(string text, int pageNumber)
    {
        var questions = new List<ParsedQuestion>();
        var lines = text.Split('\n');

        string? currentQuestionId = null;
        var stemBuilder = new StringBuilder();
        var options = new Dictionary<string, string>();
        var questionStartOffset = 0;
        var lineOffset = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            var questionMatch = QuestionNumberRegex().Match(trimmedLine);
            var optionMatch = OptionRegex().Match(trimmedLine);

            if (questionMatch.Success)
            {
                // Save previous question
                if (currentQuestionId != null)
                {
                    questions.Add(BuildParsedQuestion(
                        currentQuestionId, stemBuilder, options,
                        questionStartOffset, lineOffset));
                }

                // Start new question
                currentQuestionId = $"p{pageNumber}-q{questionMatch.Groups[1].Value}";
                stemBuilder.Clear();
                stemBuilder.Append(trimmedLine);
                options.Clear();
                questionStartOffset = lineOffset;
            }
            else if (optionMatch.Success && currentQuestionId != null)
            {
                // Option
                var optionLetter = optionMatch.Groups[1].Value.ToUpperInvariant();
                options[optionLetter] = trimmedLine;
                stemBuilder.Append(' ').Append(trimmedLine);
            }
            else if (currentQuestionId != null)
            {
                // Question continuation line
                stemBuilder.Append(' ').Append(trimmedLine);
            }

            lineOffset += line.Length + 1; // +1 for \n
        }

        // Save last question
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
