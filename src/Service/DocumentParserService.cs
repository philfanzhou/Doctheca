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

    public DocumentParserService(
        ILogger<DocumentParserService> logger,
        ILlmSegmentationService? llmSegmentation = null)
    {
        _logger = logger;
        _llmSegmentation = llmSegmentation;
    }

    public async Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default)
    {
        var sourceTypeLower = sourceType.ToLowerInvariant();

        return sourceTypeLower switch
        {
            SourceTypes.Pdf => await ParsePdfAsync(fileStream, cancellationToken),
            SourceTypes.Word => await ParseWordAsync(fileStream, cancellationToken),
            SourceTypes.Ppt => await ParsePptAsync(fileStream, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported file type: {sourceType}")
        };
    }

    #region PDF Parsing

    private async Task<ParsedDocument> ParsePdfAsync(Stream fileStream, CancellationToken cancellationToken)
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
        }

        // Store profile in result
        result.Profile = profile;

        // Second pass: segment each page
        foreach (var (pageNumber, pageText) in pageTexts)
        {
            var parsedPage = new ParsedPage { PageNumber = pageNumber };

            if (string.IsNullOrWhiteSpace(pageText))
            {
                result.Pages.Add(parsedPage);
                continue;
            }

            var blocks = MergeTextIntoBlocks(pageText);
            var globalOffset = 0;

            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                var blockText = blocks[blockIndex];
                var blockId = $"p{pageNumber}-b{blockIndex + 1}";

                // Sentence splitting with LLM fallback
                var segments = await SplitSentencesWithFallbackAsync(blockText, profile, cancellationToken);
                for (var sentIndex = 0; sentIndex < segments.Count; sentIndex++)
                {
                    var seg = segments[sentIndex];
                    parsedPage.Segments.Add(new ParsedSegment
                    {
                        BlockId = blockId,
                        SentenceId = $"{blockId}-s{sentIndex + 1}",
                        SegmentType = seg.SegmentType,
                        Text = seg.Text,
                        StartOffset = globalOffset + seg.StartOffset,
                        EndOffset = globalOffset + seg.EndOffset,
                        Tokens = Tokenize(seg.Text)
                    });
                }

                // Update globalOffset based on last segment
                if (segments.Count > 0)
                {
                    globalOffset += segments[^1].EndOffset;
                }
                else
                {
                    globalOffset += blockText.Length;
                }

                // Question boundary detection
                var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
                parsedPage.Questions.AddRange(questions);
            }

            result.Pages.Add(parsedPage);
        }

        return result;
    }

    /// <summary>
    /// Split text by newlines, merge consecutive non-empty lines into blocks (paragraphs)
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
                // Empty lines treated as paragraph separators
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

    #region Word Parsing

    private async Task<ParsedDocument> ParseWordAsync(Stream fileStream, CancellationToken cancellationToken)
    {
        var result = new ParsedDocument();

        using var doc = WordprocessingDocument.Open(fileStream, false);
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Word document missing MainDocumentPart");

        var body = mainPart.Document.Body
            ?? throw new InvalidOperationException("Word document missing Body");

        var paragraphs = body.Elements<Paragraph>().ToList();

        // Extract all blocks from paragraphs
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

        // LLM analysis
        DocumentProfile? profile = null;
        if (_llmSegmentation != null && blocks.Count > 0)
        {
            var preview = string.Join("\n\n", blocks.Take(3));
            profile = await AnalyzeDocumentAsync([(1, preview)], cancellationToken);
        }

        // Store profile in result
        result.Profile = profile;

        // Word document treated as single page
        var parsedPage = new ParsedPage { PageNumber = 1 };
        var globalOffset = 0;

        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var blockText = OcrPostProcess(blocks[blockIndex]);
            var blockId = $"p1-b{blockIndex + 1}";

            var segments = await SplitSentencesWithFallbackAsync(blockText, profile, cancellationToken);
            for (var sentIndex = 0; sentIndex < segments.Count; sentIndex++)
            {
                var seg = segments[sentIndex];
                parsedPage.Segments.Add(new ParsedSegment
                {
                    BlockId = blockId,
                    SentenceId = $"{blockId}-s{sentIndex + 1}",
                    SegmentType = seg.SegmentType,
                    Text = seg.Text,
                    StartOffset = globalOffset + seg.StartOffset,
                    EndOffset = globalOffset + seg.EndOffset,
                    Tokens = Tokenize(seg.Text)
                });
            }

            if (segments.Count > 0)
            {
                globalOffset += segments[^1].EndOffset;
            }
            else
            {
                globalOffset += blockText.Length;
            }

            var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
            parsedPage.Questions.AddRange(questions);
        }

        result.Pages.Add(parsedPage);
        return result;
    }

    #endregion

    #region PPT Parsing

    private async Task<ParsedDocument> ParsePptAsync(Stream fileStream, CancellationToken cancellationToken)
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
            }
        }

        // Store profile in result
        result.Profile = profile;

        // Second pass: segment each slide
        foreach (var (pageNumber, texts) in slideTexts)
        {
            var parsedPage = new ParsedPage { PageNumber = pageNumber };

            if (texts.Count == 0)
            {
                result.Pages.Add(parsedPage);
                continue;
            }

            var globalOffset = 0;
            for (var blockIndex = 0; blockIndex < texts.Count; blockIndex++)
            {
                var blockText = OcrPostProcess(texts[blockIndex]);
                var blockId = $"p{pageNumber}-b{blockIndex + 1}";

                var segments = await SplitSentencesWithFallbackAsync(blockText, profile, cancellationToken);
                for (var sentIndex = 0; sentIndex < segments.Count; sentIndex++)
                {
                    var seg = segments[sentIndex];
                    parsedPage.Segments.Add(new ParsedSegment
                    {
                        BlockId = blockId,
                        SentenceId = $"{blockId}-s{sentIndex + 1}",
                        SegmentType = seg.SegmentType,
                        Text = seg.Text,
                        StartOffset = globalOffset + seg.StartOffset,
                        EndOffset = globalOffset + seg.EndOffset,
                        Tokens = Tokenize(seg.Text)
                    });
                }

                if (segments.Count > 0)
                {
                    globalOffset += segments[^1].EndOffset;
                }
                else
                {
                    globalOffset += blockText.Length;
                }

                var questions = ExtractQuestions(blockText, blockId, ref globalOffset);
                parsedPage.Questions.AddRange(questions);
            }

            result.Pages.Add(parsedPage);
        }

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
    /// Split text into segments using LLM, with fallback to rule-based splitting.
    /// </summary>
    private async Task<List<SegmentWithOffset>> SplitSentencesWithFallbackAsync(
        string blockText,
        DocumentProfile? profile,
        CancellationToken cancellationToken)
    {
        // Try LLM segmentation if available and profile is set
        if (_llmSegmentation != null && profile != null)
        {
            try
            {
                var llmSegments = await _llmSegmentation.SegmentTextAsync(blockText, profile, cancellationToken);
                if (llmSegments.Count > 0)
                {
                    return llmSegments.Select(s => new SegmentWithOffset
                    {
                        Text = s.Text,
                        StartOffset = s.StartOffset,
                        EndOffset = s.EndOffset,
                        SegmentType = s.SegmentType
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM segmentation failed, falling back to rule-based splitting");
            }
        }

        // Fallback to rule-based splitting
        return SplitSentences(blockText).Select((text, i) => new SegmentWithOffset
        {
            Text = text,
            StartOffset = 0, // Will be recalculated by caller
            EndOffset = text.Length,
            SegmentType = SegmentTypes.Sentence
        }).ToList();
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
    /// Extract questions and options from text
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
                // Save previous question
                if (currentQuestionId != null)
                {
                    questions.Add(BuildParsedQuestion(
                        currentQuestionId, stemBuilder, options,
                        questionStartOffset, lineOffset));
                }

                // Start new question
                currentQuestionId = $"q{questionMatch.Groups[1].Value}";
                stemBuilder.Clear();
                stemBuilder.Append(trimmedLine);
                options.Clear();
                questionStartOffset = globalOffset + lineOffset;
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
