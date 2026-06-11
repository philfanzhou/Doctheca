using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

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
    /// <param name="cancellationToken">Cancellation token</param>
    Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default);
}
