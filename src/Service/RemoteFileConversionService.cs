using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Service for converting non-PDF files (DOCX, PPTX, etc.) to PDF.
/// </summary>
public interface IFileConversionService
{
    /// <summary>
    /// Check if the conversion service is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Convert a non-PDF file stream to PDF.
    /// Returns the converted PDF stream, or null if conversion fails.
    /// </summary>
    Task<Stream?> ConvertToPdfAsync(Stream sourceStream, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Configuration options for remote file conversion service (doc-converter).
/// </summary>
public class FileConversionOptions
{
    public const string SectionName = "FileConversion";

    /// <summary>Base URL of the doc-converter service (e.g. http://doc-converter:5050).</summary>
    public string BaseUrl { get; set; } = "http://doc-converter:5050";

    /// <summary>HTTP request timeout in seconds (should be slightly larger than doc-converter's internal conversion timeout).</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Service for converting non-PDF files (DOCX, PPTX, etc.) to PDF
/// by calling the remote doc-converter microservice over HTTP.
/// </summary>
public class RemoteFileConversionService : IFileConversionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RemoteFileConversionService> _logger;
    private readonly FileConversionOptions _options;

    public RemoteFileConversionService(
        HttpClient httpClient,
        IOptions<FileConversionOptions> options,
        ILogger<RemoteFileConversionService> logger)
    {
        _options = options.Value;
        _logger = logger;

        httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _httpClient = httpClient;

        _logger.LogInformation("RemoteFileConversionService configured: BaseUrl={BaseUrl}, Timeout={TimeoutSeconds}s",
            _options.BaseUrl, _options.TimeoutSeconds);
    }

    /// <inheritdoc />
    public bool IsAvailable => true; // HTTP service availability is checked lazily on first call

    /// <inheritdoc />
    public async Task<Stream?> ConvertToPdfAsync(Stream sourceStream, string fileName, CancellationToken ct = default)
    {
        // Reset stream position before reading
        if (sourceStream.CanSeek)
        {
            sourceStream.Position = 0;
        }

        using var content = new StreamContent(sourceStream);
        content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = fileName,
        };
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var formData = new MultipartFormDataContent
        {
            content,
        };

        _logger.LogInformation("Sending file {FileName} to doc-converter for PDF conversion", fileName);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync("/convert", formData, ct);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "HTTP call to doc-converter timed out for {FileName} (timeout={Timeout}s)",
                fileName, _options.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request to doc-converter failed for {FileName}: {Message}", fileName, ex.Message);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("doc-converter returned {StatusCode} for {FileName}: {Error}",
                (int)response.StatusCode, fileName, errorBody);
            return null;
        }

        var pdfBytes = await response.Content.ReadAsByteArrayAsync(ct);
        _logger.LogInformation("Successfully converted {FileName} to PDF ({Size} bytes) via doc-converter",
            fileName, pdfBytes.Length);

        return new MemoryStream(pdfBytes);
    }
}
