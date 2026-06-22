using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;

namespace Ruoyu.Study.DocRetrieval.Service;

/// <summary>
/// Client for MinerU Precision Extract API.
/// Requires Bearer Token (free 1000 pages/day).
///
/// Flow (verified by actual API testing):
///   1. Upload file to our OSS → get presigned URL
///   2. POST JSON {url, model_version, ...} + Bearer Token → {task_id}
///   3. Poll GET /api/v4/extract/task/{task_id} until state=done → {full_zip_url}
///   4. Download ZIP, extract full.md + images/
///   5. Upload images to our OSS, replace relative paths in Markdown
/// </summary>
public class MinerUPrecisionClient
{
    private const string DefaultBaseUrl = "https://mineru.net";

    private readonly HttpClient _httpClient;
    private readonly ILogger<MinerUPrecisionClient> _logger;
    private readonly MinerUOptions _options;
    private readonly IOssService? _ossService;

    public MinerUPrecisionClient(
        ILogger<MinerUPrecisionClient> logger,
        IOptions<MinerUOptions> options,
        IOssService? ossService = null)
    {
        _logger = logger;
        _options = options.Value;
        _ossService = ossService;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
            BaseAddress = new Uri(_options.BaseUrl ?? DefaultBaseUrl),
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DocRetrieval/1.0 (.NET 8)");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        if (!string.IsNullOrEmpty(_options.ApiToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }
    }

    /// <summary>
    /// Submit a file URL for parsing. Returns task_id.
    /// </summary>
    public async Task<string> SubmitUrlAsync(
        string fileUrl,
        string? dataId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ApiToken))
            throw new InvalidOperationException("MinerU API Token not configured. Set MinerU:ApiToken in appsettings.json.");

        _logger.LogInformation("Submitting URL to MinerU Precision API: {Url}", fileUrl);

        var payload = new Dictionary<string, object>
        {
            ["url"] = fileUrl,
            ["model_version"] = "vlm",
            ["is_ocr"] = true,
            ["enable_formula"] = true,
            ["enable_table"] = true,
        };

        if (!string.IsNullOrEmpty(dataId))
            payload["data_id"] = dataId;

        var jsonBody = JsonSerializer.Serialize(payload);
        using var jsonContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("/api/v4/extract/task", jsonContent, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("MinerU Precision submit failed: HTTP {StatusCode} - {Body}", (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"MinerU Precision submit failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
            throw new InvalidOperationException($"MinerU Precision submit failed (code={code}): {msg}");
        }

        if (!root.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Unexpected MinerU response: no data field. Raw: {responseBody}");

        var taskId = data.TryGetProperty("task_id", out var taskIdEl)
            ? taskIdEl.GetString()!
            : throw new InvalidOperationException($"No task_id in MinerU response. Raw: {responseBody}");

        _logger.LogInformation("MinerU Precision task submitted: {TaskId}", taskId);
        return taskId;
    }

    /// <summary>
    /// Poll task status. Returns (state, fullZipUrl, errMsg).
    /// </summary>
    public async Task<(string State, string? FullZipUrl, string? ErrMsg)> PollStatusAsync(
        string taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/v4/extract/task/{taskId}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
            throw new InvalidOperationException($"MinerU Precision poll failed (code={code}): {msg}");
        }

        if (!root.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Unexpected MinerU response: no data field. Raw: {json}");

        var state = data.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
        var fullZipUrl = data.TryGetProperty("full_zip_url", out var zipUrlEl) ? zipUrlEl.GetString() : null;
        var errMsg = data.TryGetProperty("err_msg", out var errMsgEl) ? errMsgEl.GetString() : null;

        return (state, fullZipUrl, errMsg);
    }

    /// <summary>
    /// Download ZIP, extract Markdown and images, upload images to OSS,
    /// replace relative image paths in Markdown with OSS presigned URLs.
    /// Returns the processed Markdown content and image count.
    /// </summary>
    public async Task<(string Markdown, int ImageCount)> DownloadAndProcessZipAsync(
        string zipUrl, string taskId, CancellationToken ct = default)
    {
        _logger.LogInformation("Downloading ZIP from MinerU: {Url}", zipUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var zipResponse = await client.GetAsync(zipUrl, ct);
        zipResponse.EnsureSuccessStatusCode();

        var zipBytes = await zipResponse.Content.ReadAsByteArrayAsync(ct);
        _logger.LogInformation("ZIP downloaded: {Size:F1} KB", zipBytes.Length / 1024.0);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Read full.md
        var mdEntry = archive.GetEntry("full.md")
            ?? throw new InvalidOperationException("ZIP does not contain full.md");

        string markdown;
        using (var mdStream = mdEntry.Open())
        using (var mdReader = new StreamReader(mdStream))
        {
            markdown = await mdReader.ReadToEndAsync(ct);
        }

        // Collect image entries
        var imageEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("images/") && e.Length > 0)
            .ToList();

        _logger.LogInformation("ZIP contains {ImageCount} images", imageEntries.Count);

        if (imageEntries.Count == 0 || _ossService == null)
        {
            // No images or no OSS — return Markdown as-is
            return (markdown, imageEntries.Count);
        }

        // Upload images to OSS and build replacement map
        var imageFolder = $"mineru/{taskId}";
        var replacementMap = new Dictionary<string, string>();

        foreach (var imgEntry in imageEntries)
        {
            var imgName = imgEntry.Name; // e.g., "abc123.jpg"
            var ossKey = $"{imageFolder}/{imgName}";

            using var imgStream = imgEntry.Open();
            using var ms = new MemoryStream();
            await imgStream.CopyToAsync(ms, ct);
            var imgBytes = ms.ToArray();

            var contentType = imgName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";

            await _ossService.UploadAsync(imgBytes, imgName, contentType, OssBucket.Documents, $"mineru/{taskId}");

            var presignedUrl = await _ossService.GetPresignedUrlAsync(
                $"documents/mineru/{taskId}/{imgName}", 3600);

            // Map relative path to presigned URL
            replacementMap[$"images/{imgName}"] = presignedUrl;

            _logger.LogDebug("Image uploaded: {Name} -> {Url}", imgName, presignedUrl);
        }

        // Replace relative image paths in Markdown
        foreach (var (relativePath, ossUrl) in replacementMap)
        {
            // Match: ![alt](images/xxx.jpg) or <img src="images/xxx.jpg"/>
            markdown = markdown.Replace($"({relativePath})", $"({ossUrl})");
            markdown = markdown.Replace($"src=\"{relativePath}\"", $"src=\"{ossUrl}\"");
        }

        _logger.LogInformation("Markdown processed: {ImageCount} images replaced with OSS URLs", replacementMap.Count);

        return (markdown, replacementMap.Count);
    }
}

/// <summary>
/// Configuration options for MinerU Precision API.
/// </summary>
public class MinerUOptions
{
    public const string SectionName = "MinerU";

    /// <summary>Bearer Token for Precision Extract API.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Base URL for MinerU API.</summary>
    public string? BaseUrl { get; set; } = "https://mineru.net";
}
