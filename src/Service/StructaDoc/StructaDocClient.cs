using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.DocLibrary.Service.StructaDoc;

/// <summary>
/// Default <see cref="IStructaDocClient"/> implementation over HttpClient.
/// Authentication uses the StructaDoc API-key scheme: Authorization: ApiKey &lt;credential&gt;.
/// Errors are RFC 7807 problem+json except 401/403, which carry an empty body.
/// </summary>
public sealed class StructaDocClient : IStructaDocClient
{
    private const int BlocksPageLimit = 1000;
    private const int MaxBlockPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<StructaDocClient> _logger;

    public StructaDocClient(
        HttpClient httpClient,
        IOptions<StructaDocOptions> options,
        ILogger<StructaDocClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var value = options.Value;
        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(value.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(value.BaseUrl.TrimEnd('/') + "/");
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(value.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("ApiKey", value.ApiKey);
        }
    }

    public async Task<StructaDocDocumentResponse> UploadDocumentAsync(
        string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", fileName);

        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form },
            "upload-document", ct);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await BuildErrorAsync(response, "upload-document", ct);
        }

        return await ReadJsonAsync<StructaDocDocumentResponse>(response, "upload-document", ct);
    }

    public async Task<StructaDocParseRunResponse> CreateParseRunAsync(
        Guid documentId, string idempotencyKey, Guid? providerConfigId = null, CancellationToken ct = default)
    {
        var payload = providerConfigId.HasValue
            ? JsonSerializer.Serialize(new { providerConfigId = providerConfigId.Value }, JsonOptions)
            : "{}";

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v1/documents/{documentId:D}/parse-runs")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await SendAsync(request, "create-parse-run", ct);

        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            throw await BuildErrorAsync(response, "create-parse-run", ct);
        }

        if (response.Headers.TryGetValues("Idempotency-Replayed", out var replayed)
            && replayed.FirstOrDefault() is "true")
        {
            _logger.LogInformation(
                "StructaDoc replayed idempotent parse run creation for document {DocumentId}", documentId);
        }

        return await ReadJsonAsync<StructaDocParseRunResponse>(response, "create-parse-run", ct);
    }

    public async Task<StructaDocParseRunResponse?> GetParseRunAsync(Guid parseRunId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/parse-runs/{parseRunId:D}"),
            "get-parse-run", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildErrorAsync(response, "get-parse-run", ct);
        }

        return await ReadJsonAsync<StructaDocParseRunResponse>(response, "get-parse-run", ct);
    }

    public async Task<List<StructaDocBlockResponse>> GetAllBlocksAsync(Guid parseRunId, CancellationToken ct = default)
    {
        var blocks = new List<StructaDocBlockResponse>();
        int? afterSequence = null;

        for (var page = 0; page < MaxBlockPages; page++)
        {
            var url = $"api/v1/parse-runs/{parseRunId:D}/blocks?limit={BlocksPageLimit}"
                + (afterSequence.HasValue ? $"&afterSequence={afterSequence.Value}" : string.Empty);

            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), "list-blocks", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildErrorAsync(response, "list-blocks", ct);
            }

            var result = await ReadJsonAsync<StructaDocBlockPageResponse>(response, "list-blocks", ct);
            blocks.AddRange(result.Items);

            if (result.NextSequence == null)
            {
                return blocks;
            }

            if (result.Items.Count == 0 || result.NextSequence <= afterSequence)
            {
                throw new StructaDocException(
                    $"StructaDoc blocks pagination did not advance (nextSequence={result.NextSequence}).",
                    200, isTransient: false, problemTitle: "blocks-pagination-stalled");
            }

            afterSequence = result.NextSequence;
        }

        throw new StructaDocException(
            $"StructaDoc blocks pagination exceeded {MaxBlockPages} pages for parse run {parseRunId}.",
            200, isTransient: false, problemTitle: "blocks-pagination-limit");
    }

    public async Task<List<StructaDocAssetResponse>> GetAssetsAsync(Guid parseRunId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/parse-runs/{parseRunId:D}/assets"),
            "list-assets", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildErrorAsync(response, "list-assets", ct);
        }

        return await ReadJsonAsync<List<StructaDocAssetResponse>>(response, "list-assets", ct);
    }

    public async Task<string> GetMarkdownAsync(Guid parseRunId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/parse-runs/{parseRunId:D}/markdown"),
            "get-markdown", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildErrorAsync(response, "get-markdown", ct);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<Stream> GetAssetContentAsync(Guid parseRunId, Guid assetId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/parse-runs/{parseRunId:D}/assets/{assetId:D}/content"),
            "get-asset-content", ct, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await BuildErrorAsync(response, "get-asset-content", ct);
            }
        }

        try
        {
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/v1/documents/{documentId:D}"),
            "delete-document", ct);

        // 202 = deletion accepted; 404 = already gone or never visible. Both are success.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NotFound
            or HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return;
        }

        throw await BuildErrorAsync(response, "delete-document", ct);
    }

    public async Task CancelParseRunAsync(Guid parseRunId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"api/v1/parse-runs/{parseRunId:D}/cancel"),
            "cancel-parse-run", ct);

        // 202 = cancel accepted; 409 = already terminal; 404 = gone. All acceptable for best-effort cancel.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.Conflict
            or HttpStatusCode.NotFound or HttpStatusCode.OK)
        {
            return;
        }

        throw await BuildErrorAsync(response, "cancel-parse-run", ct);
    }

    public async Task DeleteParseRunAsync(Guid parseRunId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/v1/parse-runs/{parseRunId:D}"),
            "delete-parse-run", ct);

        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NotFound
            or HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return;
        }

        throw await BuildErrorAsync(response, "delete-parse-run", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _httpClient.SendAsync(request, completionOption, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StructaDocException($"StructaDoc {operation} request failed due to a network error.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new StructaDocException($"StructaDoc {operation} request timed out.", ex);
        }
    }

    private async Task<StructaDocException> BuildErrorAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var statusCode = (int)response.StatusCode;
        var isTransient = statusCode is 408 or 429 || statusCode >= 500;

        // 401/403 responses carry an empty body by contract; never attempt to parse them.
        if (statusCode is 401 or 403)
        {
            _logger.LogError(
                "StructaDoc {Operation} was rejected with HTTP {StatusCode}; check StructaDoc:ApiKey and its scopes",
                operation, statusCode);
            return new StructaDocException(
                $"StructaDoc {operation} was rejected with HTTP {statusCode}.",
                statusCode, isTransient: false);
        }

        string? title = null;
        string? detail = null;
        string? code = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                var problem = JsonSerializer.Deserialize<StructaDocProblemDetails>(body, JsonOptions);
                title = problem?.Title;
                detail = problem?.Detail;
                code = problem?.Code;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse StructaDoc problem details for {Operation}", operation);
        }

        var message = $"StructaDoc {operation} failed with HTTP {statusCode}"
            + (title != null ? $" ({title})" : string.Empty)
            + (detail != null ? $": {detail}" : string.Empty);

        return new StructaDocException(message, statusCode, isTransient, title, code);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new StructaDocException(
                    $"StructaDoc {operation} returned an empty body.", (int)response.StatusCode, isTransient: false);
        }
        catch (JsonException ex)
        {
            throw new StructaDocException(
                $"StructaDoc {operation} returned invalid JSON: {ex.Message}",
                (int)response.StatusCode, isTransient: false);
        }
    }
}
