using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

/// <summary>
/// 文档解析服务接口
/// </summary>
public interface IDocumentParserService
{
    /// <summary>
    /// 解析文档，返回结构化的解析结果
    /// </summary>
    /// <param name="fileStream">文件流</param>
    /// <param name="sourceType">文件类型（pdf/docx/pptx）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default);
}
