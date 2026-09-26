using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Doctheca.Service.StructaDoc;
using Xunit;

namespace Doctheca.Tests.StructaDoc;

public class StructaDocClientTests
{
    private const string ApiKey = "sd1.00000000000000000000000000000000.test-secret";

    [Fact]
    public async Task UploadDocumentAsync_SendsMultipartWithApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return JsonResponse("""
                {
                  "id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                  "originalFileName": "paper.pdf",
                  "mediaType": "application/pdf",
                  "extension": ".pdf",
                  "sizeBytes": 3,
                  "sha256": "abc",
                  "createdAt": "2026-09-21T08:00:00Z",
                  "latestParseStatus": null,
                  "ownedByCurrentUser": false
                }
                """, HttpStatusCode.Created);
        });

        using var content = new MemoryStream([1, 2, 3]);
        var result = await client.UploadDocumentAsync("paper.pdf", "application/pdf", content);

        result.Id.Should().Be(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        result.MediaType.Should().Be("application/pdf");
        captured!.RequestUri!.PathAndQuery.Should().Be("/api/v1/documents");
        captured.Headers.Authorization!.Scheme.Should().Be("ApiKey");
        captured.Headers.Authorization.Parameter.Should().Be(ApiKey);
        captured.Content!.Should().BeOfType<MultipartFormDataContent>();
    }

    [Fact]
    public async Task UploadDocumentAsync_UnsupportedType_SurfacesProblemCode()
    {
        var client = CreateClient(_ => JsonResponse(
            """
            { "type": "about:blank", "title": "Document upload failed", "status": 415, "code": "unsupported-document-type" }
            """,
            HttpStatusCode.UnsupportedMediaType));

        var act = () => client.UploadDocumentAsync("a.pdf", "application/pdf", new MemoryStream([1]));

        var ex = await act.Should().ThrowAsync<StructaDocException>();
        ex.Which.ProblemCode.Should().Be("unsupported-document-type");
        ex.Which.StatusCode.Should().Be(415);
        ex.Which.IsTransient.Should().BeFalse();
    }

    [Fact]
    public async Task CreateParseRunAsync_SendsIdempotencyKeyAndProviderConfig()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = CreateClientAsync(async request =>
        {
            captured = request;
            body = request.Content != null ? await request.Content.ReadAsStringAsync() : null;
            return JsonResponse(ParseRunJson("running"), HttpStatusCode.Created);
        });

        var providerConfigId = Guid.NewGuid();
        var run = await client.CreateParseRunAsync(Guid.NewGuid(), "idem-key-1", providerConfigId);

        run.Status.Should().Be("running");
        captured!.Headers.GetValues("Idempotency-Key").Should().ContainSingle("idem-key-1");
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("providerConfigId").GetGuid().Should().Be(providerConfigId);
    }

    [Fact]
    public async Task CreateParseRunAsync_ReplayedRequest_ReturnsOriginalRun()
    {
        var response = JsonResponse(ParseRunJson("queued"), HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Idempotency-Replayed", "true");
        var client = CreateClient(_ => response);

        var run = await client.CreateParseRunAsync(Guid.NewGuid(), "idem-key-1");

        run.Status.Should().Be("queued");
    }

    [Fact]
    public async Task GetParseRunAsync_NotFound_ReturnsNull()
    {
        var client = CreateClient(_ => JsonResponse(
            """{ "title": "Parse Run not found", "status": 404 }""", HttpStatusCode.NotFound));

        var result = await client.GetParseRunAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllBlocksAsync_FollowsSequencePagination()
    {
        var calls = 0;
        var client = CreateClient(request =>
        {
            calls++;
            request.RequestUri!.Query.Should().Contain("limit=1000");
            if (calls == 1)
            {
                request.RequestUri.Query.Should().NotContain("afterSequence");
                return JsonResponse("""
                    { "items": [ { "id": "00000000-0000-0000-0000-000000000001", "sequence": 0, "pageNumber": 1, "type": "text", "content": "a" } ], "nextSequence": 0 }
                    """);
            }

            request.RequestUri.Query.Should().Contain("afterSequence=0");
            return JsonResponse("""
                { "items": [ { "id": "00000000-0000-0000-0000-000000000002", "sequence": 1, "pageNumber": 1, "type": "image", "assetId": "00000000-0000-0000-0000-000000000009" } ], "nextSequence": null }
                """);
        });

        var blocks = await client.GetAllBlocksAsync(Guid.NewGuid());

        calls.Should().Be(2);
        blocks.Should().HaveCount(2);
        blocks[1].AssetId.Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000009"));
    }

    [Fact]
    public async Task GetAllBlocksAsync_StalledPagination_Throws()
    {
        var client = CreateClient(_ => JsonResponse("""
            { "items": [], "nextSequence": 5 }
            """));

        var act = () => client.GetAllBlocksAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<StructaDocException>();
    }

    [Fact]
    public async Task GetAssetsAsync_DeserializesBareArray()
    {
        var client = CreateClient(_ => JsonResponse("""
            [ { "id": "00000000-0000-0000-0000-000000000009", "name": "img.jpg", "mediaType": "image/jpeg", "sizeBytes": 10, "sha256": "x", "width": 100, "height": 50 } ]
            """));

        var assets = await client.GetAssetsAsync(Guid.NewGuid());

        assets.Should().ContainSingle();
        assets[0].Name.Should().Be("img.jpg");
        assets[0].Width.Should().Be(100);
    }

    [Fact]
    public async Task GetMarkdownAsync_ReturnsContent()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Title", Encoding.UTF8, "text/markdown"),
        });

        var markdown = await client.GetMarkdownAsync(Guid.NewGuid());

        markdown.Should().Be("# Title");
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DeleteDocumentAsync_AcceptsCompletedLifecycleStates(HttpStatusCode status)
    {
        var client = CreateClient(_ => new HttpResponseMessage(status));

        await client.DeleteDocumentAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task DeleteDocumentAsync_Conflict_Throws()
    {
        var client = CreateClient(_ => JsonResponse(
            """{ "title": "Resource is active", "status": 409 }""", HttpStatusCode.Conflict));

        var act = () => client.DeleteDocumentAsync(Guid.NewGuid());

        var ex = await act.Should().ThrowAsync<StructaDocException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.IsTransient.Should().BeFalse();
    }

    [Fact]
    public async Task Unauthorized_EmptyBody_ThrowsWithoutParsing()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var act = () => client.GetMarkdownAsync(Guid.NewGuid());

        var ex = await act.Should().ThrowAsync<StructaDocException>();
        ex.Which.StatusCode.Should().Be(401);
        ex.Which.IsTransient.Should().BeFalse();
        ex.Which.ProblemTitle.Should().BeNull();
    }

    [Fact]
    public async Task ServerError_IsTransient()
    {
        var client = CreateClient(_ => JsonResponse(
            """{ "title": "boom", "status": 503 }""", HttpStatusCode.ServiceUnavailable));

        var act = () => client.GetParseRunAsync(Guid.NewGuid());

        var ex = await act.Should().ThrowAsync<StructaDocException>();
        ex.Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task NetworkFailure_IsTransient()
    {
        var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        var act = () => client.GetParseRunAsync(Guid.NewGuid());

        var ex = await act.Should().ThrowAsync<StructaDocException>();
        ex.Which.IsTransient.Should().BeTrue();
        ex.Which.StatusCode.Should().Be(0);
    }

    // ── helpers ──

    private static StructaDocClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        CreateClientAsync(request => Task.FromResult(handler(request)));

    private static StructaDocClient CreateClientAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://structadoc.test/"),
        };
        var options = Options.Create(new StructaDocOptions
        {
            BaseUrl = "http://structadoc.test",
            ApiKey = ApiKey,
        });
        return new StructaDocClient(httpClient, options, NullLogger<StructaDocClient>.Instance);
    }

    private static string ParseRunJson(string status) => $$"""
        {
          "id": "4f2504e0-4f89-11d3-9a0c-0305e82c3302",
          "documentId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
          "status": "{{status}}",
          "stage": null,
          "providerType": "mineru-cloud",
          "providerConfigId": "00000000-0000-0000-0000-0000000000aa",
          "providerConfigVersionId": "00000000-0000-0000-0000-0000000000bb",
          "sourceMediaType": "application/pdf",
          "submittedMediaType": "application/pdf",
          "attemptCount": 1,
          "maxAttempts": 3,
          "nextAttemptAt": "2026-09-21T08:00:05Z",
          "errorCode": null,
          "errorMessage": null,
          "createdAt": "2026-09-21T08:00:00Z",
          "startedAt": null,
          "completedAt": null
        }
        """;

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/problem+json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
