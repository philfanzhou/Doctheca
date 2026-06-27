using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

/// <summary>
/// Document parsing service interface
/// </summary>
public interface IDocumentParserService
{
    /// <summary>
    /// Parse document and return structured parsing result
    /// </summary>
    /// <param name="fileStream">File stream</param>
    /// <param name="sourceType">File type (pdf/docx/pptx)</param>
    /// <param name="progress">Optional progress callback for reporting parsing progress</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, IProgress<ParsingProgress>? progress = null, CancellationToken cancellationToken = default);
}
