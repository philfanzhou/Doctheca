using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;

namespace Ruoyu.Study.DocLibrary.Service;

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

    public MinerUPrecisionClient(
        ILogger<MinerUPrecisionClient> logger,
        IOptions<MinerUOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
            BaseAddress = new Uri(_options.BaseUrl ?? DefaultBaseUrl),
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DocLibrary/1.0 (.NET 8)");
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
        string? modelVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ApiToken))
            throw new InvalidOperationException("MinerU API Token not configured. Set MinerU:ApiToken in appsettings.json.");

        _logger.LogInformation("Submitting URL to MinerU Precision API: {Url}", fileUrl);

        var payload = new Dictionary<string, object>
        {
            ["url"] = fileUrl,
            ["model_version"] = modelVersion ?? _options.ModelVersion ?? "vlm",
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
    /// Download ZIP, extract Markdown + content_list.json + images + layout.pdf,
    /// upload images to OSS, replace relative image paths in Markdown with S3 paths.
    /// Returns the full parse result (markdown + content_list JSON + images + raw ZIP + layout.pdf).
    /// </summary>
    public async Task<MinerUParseResult> DownloadAndProcessZipAsync(
        string zipUrl, string taskId, IOssService ossService, CancellationToken ct = default)
    {
        _logger.LogInformation("Downloading ZIP from MinerU: {Url}", zipUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var zipResponse = await client.GetAsync(zipUrl, ct);
        zipResponse.EnsureSuccessStatusCode();

        var zipBytes = await zipResponse.Content.ReadAsByteArrayAsync(ct);
        _logger.LogInformation("ZIP downloaded: {Size:F1} KB", zipBytes.Length / 1024.0);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Log all ZIP entries for diagnostics
        _logger.LogInformation("ZIP entries for task {TaskId}: {Entries}",
            taskId, string.Join(", ", archive.Entries.Select(e => $"{e.FullName}({e.Length}B)")));

        // Read full.md
        var mdEntry = archive.GetEntry("full.md")
            ?? throw new InvalidOperationException("ZIP does not contain full.md");

        string markdown;
        using (var mdStream = mdEntry.Open())
        using (var mdReader = new StreamReader(mdStream))
        {
            markdown = await mdReader.ReadToEndAsync(ct);
        }

        // Read content_list.json (structured per-block data)
        // MinerU API returns {filename}_content_list.json, not just content_list.json
        // Also check subdirectories: {subdir}/content_list.json or {subdir}/{filename}_content_list.json
        var contentListJson = ReadZipEntryAsString(archive, "content_list.json")
            ?? archive.Entries
                .Where(e => e.FullName.EndsWith("_content_list.json", StringComparison.OrdinalIgnoreCase))
                .Select(e => ReadZipEntryAsString(archive, e.FullName))
                .FirstOrDefault()
            ?? archive.Entries
                .Where(e => e.FullName.EndsWith("/content_list.json", StringComparison.OrdinalIgnoreCase))
                .Select(e => ReadZipEntryAsString(archive, e.FullName))
                .FirstOrDefault()
            ?? "[]";
        if (contentListJson == "[]")
        {
            _logger.LogWarning("ZIP does not contain content_list.json, structured data unavailable for task {TaskId}", taskId);
        }

        // Read layout.pdf (annotated PDF with layout boxes) — optional
        // Note: layout.pdf is only produced by the "pipeline" model, not "vlm"
        // Matching order: exact "layout.pdf" → any path ending with "_layout.pdf" →
        // any path ending with "/layout.pdf" (MinerU may place it in a subdirectory)
        var layoutPdf = ReadZipEntryAsBytes(archive, "layout.pdf")
            ?? archive.Entries
                .Where(e => e.FullName.EndsWith("_layout.pdf", StringComparison.OrdinalIgnoreCase))
                .Select(e => ReadZipEntryAsBytes(archive, e.FullName))
                .FirstOrDefault()
            ?? archive.Entries
                .Where(e => e.FullName.Equals("layout.pdf", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith("/layout.pdf", StringComparison.OrdinalIgnoreCase))
                .Select(e => ReadZipEntryAsBytes(archive, e.FullName))
                .FirstOrDefault();
        if (layoutPdf == null)
        {
            _logger.LogWarning("ZIP does not contain layout.pdf for task {TaskId} (model={ModelVersion})", taskId, "pipeline");
        }
        else
        {
            _logger.LogInformation("Layout PDF found in ZIP for task {TaskId}: {Size} bytes", taskId, layoutPdf.Length);
        }

        // Collect image entries
        var imageEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("images/") && e.Length > 0)
            .ToList();

        _logger.LogInformation("ZIP contains {ImageCount} images, contentList={ContentListBytes}B, layoutPdf={HasLayout}",
            imageEntries.Count, contentListJson.Length, layoutPdf != null ? "yes" : "no");

        var imageMetadataList = new List<ImageMetadata>();
        if (imageEntries.Count > 0)
        {
            // Upload images to OSS and build replacement map (relative path -> S3 path)
            var replacementMap = new Dictionary<string, string>();

            foreach (var imgEntry in imageEntries)
            {
                var imgName = imgEntry.Name;

                using var imgStream = imgEntry.Open();
                using var ms = new MemoryStream();
                await imgStream.CopyToAsync(ms, ct);
                var imgBytes = ms.ToArray();

                var contentType = imgName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "image/jpeg";

                var ossPath = await ossService.UploadAsync(imgBytes, imgName, contentType, OssBucket.Documents, $"mineru/{taskId}");

                replacementMap[$"images/{imgName}"] = ossPath;
                imageMetadataList.Add(new ImageMetadata(imgName, ossPath, contentType, imgBytes.Length));

                _logger.LogDebug("Image uploaded: {Name} -> {Path}", imgName, ossPath);
            }

            // Replace relative image paths in Markdown with S3 paths
            foreach (var (relativePath, s3Path) in replacementMap)
            {
                // Match: ![alt](images/xxx.jpg) or <img src="images/xxx.jpg"/>
                markdown = markdown.Replace($"({relativePath})", $"({s3Path})");
                markdown = markdown.Replace($"src=\"{relativePath}\"", $"src=\"{s3Path}\"");
            }
        }

        return new MinerUParseResult(
            ZipBytes: zipBytes,
            Markdown: markdown,
            ContentListJson: contentListJson,
            Images: imageMetadataList,
            LayoutPdf: layoutPdf);
    }

    private static string? ReadZipEntryAsString(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry == null) return null;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[]? ReadZipEntryAsBytes(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry == null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// All parsed artifacts from a single MinerU task ZIP.
/// </summary>
public record MinerUParseResult(
    byte[] ZipBytes,
    string Markdown,
    string ContentListJson,
    List<ImageMetadata> Images,
    byte[]? LayoutPdf);

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

    /// <summary>Model version: "vlm" (recommended, default), "pipeline" (produces layout.pdf), or "MinerU-HTML".</summary>
    public string? ModelVersion { get; set; }
}
