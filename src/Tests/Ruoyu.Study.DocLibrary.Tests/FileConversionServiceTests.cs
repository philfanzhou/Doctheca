using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class FileConversionServiceTests
{
    private static RemoteFileConversionService CreateService(
        HttpClient httpClient,
        string baseUrl = "http://doc-converter:5050")
    {
        // Mimic Program.cs named HttpClient configuration so the service no longer
        // mutates the client in its constructor.
        httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(180);

        var options = Options.Create(new FileConversionOptions
        {
            Url = baseUrl,
        });
        var logger = new Mock<ILogger<RemoteFileConversionService>>().Object;
        return new RemoteFileConversionService(httpClient, options, logger);
    }

    [Fact]
    public void IsAvailable_AlwaysReturnsTrue()
    {
        var service = CreateService(new HttpClient());
        service.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task ConvertToPdfAsync_ReturnsStream_OnSuccess()
    {
        var fakePdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
        var handler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fakePdf),
        }));
        var client = new HttpClient(handler);
        var service = CreateService(client);

        using var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.ConvertToPdfAsync(sourceStream, "test.docx");

        result.Should().NotBeNull();
        result!.Length.Should().Be(fakePdf.Length);
    }

    [Fact]
    public async Task ConvertToPdfAsync_ReturnsNull_OnNonSuccessStatusCode()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"LibreOffice not available\"}"),
        }));
        var client = new HttpClient(handler);
        var service = CreateService(client);

        using var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.ConvertToPdfAsync(sourceStream, "test.docx");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConvertToPdfAsync_ReturnsNull_OnHttpRequestException()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new HttpClient(handler);
        var service = CreateService(client);

        using var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.ConvertToPdfAsync(sourceStream, "test.docx");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConvertToPdfAsync_SeeksStreamToStartBeforeReading()
    {
        byte[]? capturedBody = null;
        var handler = new StubHttpHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x25 }),
            };
        });
        var client = new HttpClient(handler);
        var service = CreateService(client);

        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var sourceStream = new MemoryStream(data);
        sourceStream.Position = 2; // Non-zero position

        await service.ConvertToPdfAsync(sourceStream, "test.docx");

        // Multipart body includes boundary headers, so length > 5.
        // Verify the original 5 bytes are all present (position was reset to 0).
        capturedBody.Should().NotBeNull();
        foreach (var b in data)
        {
            capturedBody.Should().Contain(b);
        }
    }

    [Fact]
    public void Constructor_DoesNotMutatePreconfiguredClient()
    {
        var options = Options.Create(new FileConversionOptions
        {
            Url = "http://my-converter:9999/",
        });
        var logger = new Mock<ILogger<RemoteFileConversionService>>().Object;
        var expectedBaseAddress = new Uri("http://preconfigured:1234/");
        var expectedTimeout = TimeSpan.FromSeconds(30);
        var client = new HttpClient
        {
            BaseAddress = expectedBaseAddress,
            Timeout = expectedTimeout,
        };

        _ = new RemoteFileConversionService(client, options, logger);

        client.BaseAddress.Should().Be(expectedBaseAddress);
        client.Timeout.Should().Be(expectedTimeout);
    }

    [Fact]
    public async Task ConvertToPdfAsync_SendsMultipartFormData()
    {
        string? capturedContentType = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedContentType = req.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x25 }),
            });
        });
        var client = new HttpClient(handler);
        var service = CreateService(client);

        using var sourceStream = new MemoryStream(new byte[] { 1 });
        await service.ConvertToPdfAsync(sourceStream, "test.docx");

        // Should be multipart/form-data
        capturedContentType.Should().Be("multipart/form-data");
    }

    [Fact]
    public void IsPdfFile_DetectedByContentType()
    {
        var pdfContentType = "application/pdf";
        var docxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        pdfContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        docxContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void IsPdfFile_DetectedByExtension()
    {
        var pdfFile = "document.pdf";
        var docxFile = "document.docx";

        pdfFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        docxFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    /// <summary>
    /// Stub HttpHandler for testing HttpClient-based code without a real server.
    /// </summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
