using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Service for splitting large PDF files into smaller chunks
/// to comply with MinerU's 200-page limit.
/// </summary>
public interface IPdfSplitService
{
    /// <summary>
    /// Get the number of pages in a PDF file.
    /// </summary>
    int GetPageCount(Stream pdfStream);

    /// <summary>
    /// Split a PDF into chunks of at most maxPagesPerChunk pages.
    /// Returns a list of (chunkIndex, chunkStream) pairs.
    /// </summary>
    List<(int ChunkIndex, Stream ChunkStream)> SplitPdf(Stream pdfStream, int maxPagesPerChunk = 200);
}

public class PdfSplitService : IPdfSplitService
{
    private readonly ILogger<PdfSplitService> _logger;

    public PdfSplitService(ILogger<PdfSplitService> logger)
    {
        _logger = logger;
    }

    public int GetPageCount(Stream pdfStream)
    {
        pdfStream.Position = 0;
        using var doc = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public List<(int ChunkIndex, Stream ChunkStream)> SplitPdf(Stream pdfStream, int maxPagesPerChunk = 200)
    {
        pdfStream.Position = 0;
        using var doc = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Import);
        var totalPages = doc.PageCount;
        var chunks = new List<(int ChunkIndex, Stream ChunkStream)>();

        _logger.LogInformation("Splitting PDF: {TotalPages} pages into chunks of {MaxPages}", totalPages, maxPagesPerChunk);

        for (int startPage = 0; startPage < totalPages; startPage += maxPagesPerChunk)
        {
            var chunkIndex = startPage / maxPagesPerChunk;
            var endPage = Math.Min(startPage + maxPagesPerChunk, totalPages);

            var chunk = new PdfDocument();
            for (int i = startPage; i < endPage; i++)
            {
                chunk.AddPage(doc.Pages[i]);
            }

            var ms = new MemoryStream();
            chunk.Save(ms, false);
            ms.Position = 0;

            chunks.Add((chunkIndex, ms));
            _logger.LogDebug("Chunk {Index}: pages {Start}-{End}", chunkIndex, startPage + 1, endPage);
        }

        _logger.LogInformation("PDF split into {ChunkCount} chunks", chunks.Count);
        return chunks;
    }
}
