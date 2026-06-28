using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Client for MinerU Agent Lightweight Extract API.
/// No Token required, IP rate-limited.
///
/// File upload flow (verified by actual API testing):
///   1. POST JSON {file_name, is_ocr, enable_formula, enable_table} → {task_id, file_url}
///   2. PUT raw bytes to file_url (NO Content-Type header) → HTTP 200
///   3. Poll GET /api/v1/agent/parse/{task_id} until state=done → {markdown_url}
/// </summary>
public class MinerUAgentClient
{
    private const string BaseUrl = "https://mineru.net";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MinerUAgentClient> _logger;

    public MinerUAgentClient(ILogger<MinerUAgentClient> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
            BaseAddress = new Uri(BaseUrl),
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DocLibrary/1.0 (.NET 8)");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Submit a file for parsing. Returns task_id.
    /// </summary>
    public async Task<string> SubmitFileAsync(
        string fileName,
        byte[] fileBytes,
        bool enableOcr = false,
        bool enableFormula = true,
        bool enableTable = true,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Submitting file {FileName} ({Size:F1} KB) to MinerU Agent API",
            fileName, fileBytes.Length / 1024.0);

        // Step 1: POST JSON body to get task_id and OSS presigned URL
        var payload = new
        {
            file_name = fileName,
            is_ocr = enableOcr,
            enable_formula = enableFormula,
            enable_table = enableTable,
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var jsonContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("/api/v1/agent/parse/file", jsonContent, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("MinerU submit failed: HTTP {StatusCode} - {Body}", (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"MinerU submit failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
            throw new InvalidOperationException($"MinerU submit failed (code={code}): {msg}");
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Unexpected MinerU response: no data field. Raw: {responseBody}");
        }

        var taskId = data.TryGetProperty("task_id", out var taskIdEl)
            ? taskIdEl.GetString()!
            : throw new InvalidOperationException($"No task_id in MinerU response. Raw: {responseBody}");

        // Step 2: PUT raw file bytes to OSS presigned URL
        if (!data.TryGetProperty("file_url", out var fileUrlEl))
        {
            _logger.LogWarning("No file_url in MinerU response for task {TaskId}, skipping OSS upload", taskId);
            return taskId;
        }

        var fileUrl = fileUrlEl.GetString()!;
        _logger.LogInformation("Uploading file to OSS for task {TaskId}", taskId);

        await UploadToOssAsync(fileUrl, fileBytes, ct);
        _logger.LogInformation("OSS upload complete for task {TaskId}", taskId);

        return taskId;
    }

    /// <summary>
    /// Poll task status. Returns (state, markdownUrl, progress).
    /// </summary>
    public async Task<(string State, string? MarkdownUrl, int ExtractedPages, int TotalPages)> PollStatusAsync(
        string taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/v1/agent/parse/{taskId}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "Unknown error";
            throw new InvalidOperationException($"MinerU poll failed (code={code}): {msg}");
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Unexpected MinerU response: no data field. Raw: {json}");
        }

        var state = data.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
        var markdownUrl = data.TryGetProperty("markdown_url", out var mdUrlEl) ? mdUrlEl.GetString() : null;

        var extractedPages = 0;
        var totalPages = 0;
        if (data.TryGetProperty("extract_progress", out var progressEl))
        {
            extractedPages = progressEl.TryGetProperty("extracted_pages", out var ext) ? ext.GetInt32() : 0;
            totalPages = progressEl.TryGetProperty("total_pages", out var tot) ? tot.GetInt32() : 0;
        }

        return (state, markdownUrl, extractedPages, totalPages);
    }

    /// <summary>
    /// Download Markdown content from CDN URL.
    /// </summary>
    public async Task<string> DownloadMarkdownAsync(string cdnUrl, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var response = await client.GetAsync(cdnUrl, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Upload raw bytes to OSS presigned URL.
    /// CRITICAL: Do NOT set Content-Type — the signature is based on empty Content-Type.
    /// </summary>
    private async Task UploadToOssAsync(string presignedUrl, byte[] fileBytes, CancellationToken ct)
    {
        using var content = new ByteArrayContent(fileBytes);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var response = await client.PutAsync(presignedUrl, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OSS upload failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }
    }
}
