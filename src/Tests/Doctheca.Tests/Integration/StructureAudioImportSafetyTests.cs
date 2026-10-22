using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Service.Audio;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class StructureAudioEndpointTests
{
    [Theory]
    [InlineData("DocumentAudio:MaxFileBytes", "0")]
    [InlineData("DocumentAudio:MaxArchiveBytes", "1")]
    [InlineData("DocumentAudio:MaxArchiveBytes", "9223372036854775807")]
    public async Task InvalidLimitsDisableImportsWithoutBreakingHostStartup(string key, string value)
    {
        using var factory = Factory(extra: new() { [key] = value });
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var list = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!;
        list["storage"]!["available"]!.GetValue<bool>().Should().BeFalse();
        var response = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/SectionA.mp3", Mp3("a"))));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        AssertNoStorageDetails(await response.Content.ReadAsStringAsync());
        (await CountAudioAsync(factory, parseId)).Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportDoesNotRequireAPublicStorageAddress()
    {
        using var factory = Factory(extra: new() { ["Oss:PublicBaseUrl"] = "" });
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var response = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/SectionA.mp3", Mp3("a"))));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStorageDetails(await response.Content.ReadAsStringAsync());
        (await CountAudioAsync(factory, parseId)).Should().Be(1);
        (await minio.KeysAsync(_bucket)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("disabled", 503)]
    [InlineData("missing", 404)]
    [InlineData("oversized", 413)]
    public async Task UploadPreflightRefusesBeforeReadingTheBody(string scenario, int status)
    {
        using var factory = Factory(storage: scenario != "disabled");
        using var client = Admin(factory);
        var (_, existing) = await SeedTextbookAsync(factory, client);
        var parseId = scenario == "missing" ? Guid.NewGuid().ToString() : existing;
        var body = new Mock<Stream>(MockBehavior.Strict);
        body.SetupGet(stream => stream.CanRead).Returns(true);
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = $"/admin/document-parses/{parseId}/audio";
            context.Request.Headers.Authorization = "Bearer " + CreateToken("admin");
            context.Request.ContentType = "multipart/form-data; boundary=test";
            context.Request.ContentLength = scenario == "oversized" ? MaxArchiveBytes + DocumentAudioOptions.MultipartOverheadBytes + 1 : 100;
            context.Request.Body = body.Object;
        });
        response.Response.StatusCode.Should().Be(status);
        body.Invocations.Should().BeEmpty("no body member is accessed before a preflight refusal");
        (await CountAudioAsync(factory, existing)).Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task MultipartWithoutContentLengthStopsAtTheWholeRequestCap()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        using var content = Upload(Enumerable.Range(0, 64).Select(i => ($"unit1/{i}.mp3", Mp3("a", (int)MaxFileBytes))).ToArray());
        var bytes = await content.ReadAsByteArrayAsync();
        using var body = new MemoryStream(bytes);
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = $"/admin/document-parses/{parseId}/audio";
            context.Request.Headers.Authorization = "Bearer " + CreateToken("admin");
            context.Request.ContentType = content.Headers.ContentType!.ToString();
            context.Request.ContentLength = null;
            context.Request.Body = body;
        });
        response.Response.StatusCode.Should().Be(413);
        body.Position.Should().BeLessThan(bytes.Length, "the entire oversized body must not be buffered");
        (await CountAudioAsync(factory, parseId)).Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task ExpandedBytesAreBoundedAcrossArchivesAndLooseFiles()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var first = Zip(Enumerable.Range(0, 8).Select(i => ($"first/{i}.mp3", Mp3("a", (int)MaxFileBytes))));
        var second = Zip(Enumerable.Range(0, 8).Select(i => ($"second/{i}.mp3", Mp3("b", (int)MaxFileBytes))));
        var response = await client.PostAsync($"/admin/document-parses/{parseId}/audio",
            Upload(("first.zip", first), ("second.zip", second), ("extra.mp3", Mp3("extra"))));
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        (await CountAudioAsync(factory, parseId)).Should().Be(0);
    }

    [Fact]
    public async Task FileAndEntryCountsAndNormalizedDuplicatesRejectTheWholeBatch()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var cases = new[]
        {
            Upload(("too-many.zip", Zip(Enumerable.Range(0, 1001).Select(i => ($"{i}.mp3", Mp3("a", 12)))))),
            Upload(("entries.zip", Zip(Enumerable.Range(0, 4001).Select(i => ($"{i}/.DS_Store", Array.Empty<byte>()))))),
            Upload(("a.zip", Zip([("unit1/caf\u00e9.mp3", Mp3("a")), ("unit1/cafe\u0301.mp3", Mp3("b"))]))),
            Upload(("a.zip", Zip([("unit1/track.mp3", Mp3("a")), ("unit1\\track.mp3", Mp3("b"))]))),
        };
        foreach (var content in cases)
        {
            var response = await client.PostAsync($"/admin/document-parses/{parseId}/audio", content);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            AssertNoStorageDetails(await response.Content.ReadAsStringAsync());
            (await minio.KeysAsync(_bucket)).Should().BeEmpty();
            (await CountAudioAsync(factory, parseId)).Should().Be(0);
        }
    }

    [Fact]
    public async Task ReviewFailureIsAtomicAndDuplicateIdsAreInvalid()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var imported = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio",
            Upload(("unit1/SectionA.mp3", Mp3("a")), ("unit1/SectionB.mp3", Mp3("b"))))))!["items"]!.AsArray();
        var first = imported[0]!["id"]!.GetValue<string>();
        var second = imported[1]!["id"]!.GetValue<string>();
        var rejected = await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new
        {
            links = new[] { new { id = first, nodeId = (string?)null }, new { id = second, nodeId = (string?)Guid.NewGuid().ToString() } },
            removeIds = new[] { first },
        });
        rejected.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var duplicate = await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new
        {
            links = new[] { new { id = first, nodeId = (string?)null }, new { id = first, nodeId = (string?)null } },
        });
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reloaded = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!["items"]!.AsArray();
        reloaded.ToJsonString().Should().Be(imported.ToJsonString());
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentImportsKeepOneBatchAndCleanTheLoser()
    {
        var logs = new RequestScopeCapture();
        using var factory = Factory(logs: logs);
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/SectionA.mp3", Mp3("same"))))));
        responses.Select(response => response.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, string.Join("; ", logs.Logs.Select(entry => entry.Message).Where(message => message.Contains("Audio") || message.Contains("audio"))));
        (await minio.KeysAsync(_bucket)).Should().ContainSingle();
        (await CountAudioAsync(factory, parseId)).Should().Be(1);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("upload")]
    [InlineData("database")]
    [InlineData("cancel-before-commit")]
    [InlineData("cancel-after-commit")]
    [InlineData("cancel-during-reload")]
    [InlineData("unknown-commit")]
    [InlineData("unknown-reference-before-commit")]
    [InlineData("cancel-upload")]
    [InlineData("parse-removed")]
    public async Task InterruptedImportsCompensateOnlyConfirmedUnreferencedObjects(string failure)
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseText) = await SeedTextbookAsync(factory, client);
        var parseId = Guid.Parse(parseText);
        using var scope = factory.Services.CreateScope();
        var actual = scope.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>();
        var repo = new Mock<IDocumentStructureAudioRepository>();
        repo.Setup(r => r.GetExistingSourcePathsAsync(parseId, It.IsAny<IReadOnlyCollection<string>>())).ReturnsAsync([]);
        repo.Setup(r => r.GetReferencedObjectKeysAsync(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns((IReadOnlyCollection<string> keys) => failure.StartsWith("unknown-")
                ? Task.FromException<HashSet<string>>(new IOException("synthetic reference outage"))
                : actual.GetReferencedObjectKeysAsync(keys));
        using var cancellation = new CancellationTokenSource();
        repo.Setup(r => r.GetByParseIdAsync(parseId)).Returns(async () =>
        {
            var rows = await actual.GetByParseIdAsync(parseId);
            if (failure == "cancel-during-reload") cancellation.Cancel();
            return rows;
        });
        repo.Setup(r => r.InsertAsync(parseId, It.IsAny<IReadOnlyList<DocumentStructureAudioModel>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, IReadOnlyList<DocumentStructureAudioModel> rows, CancellationToken token) =>
            {
                if (failure == "parse-removed")
                    (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
                if (failure == "unknown-reference-before-commit") throw new IOException("synthetic database outage");
                if (failure == "database") throw new Microsoft.EntityFrameworkCore.DbUpdateException("synthetic non-unique failure");
                if (failure == "cancel-before-commit") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                var committed = await actual.InsertAsync(id, rows, token);
                if (failure == "cancel-after-commit") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (failure == "unknown-commit") throw new IOException("synthetic lost commit acknowledgement");
                return committed;
            });
        var realStore = scope.ServiceProvider.GetRequiredService<StructureAudioStore>();
        var store = realStore;
        if (failure is "upload" or "cancel-upload")
        {
            var realOss = scope.ServiceProvider.GetRequiredService<IOssService>();
            var oss = new Mock<IOssService>();
            var uploads = 0;
            oss.Setup(o => o.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), OssBucket.Audio,
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (Stream content, string name, string type, OssBucket bucket, string? folder, CancellationToken token) =>
                {
                    var key = await realOss.UploadAsync(content, name, type, bucket, folder, token);
                    if (failure == "cancel-upload") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    if (++uploads == 2) throw new IOException("synthetic lost upload acknowledgement");
                    return key;
                });
            oss.Setup(o => o.DeleteAsync(It.IsAny<string>())).Returns((string key) => realOss.DeleteAsync(key));
            store = new StructureAudioStore(() => oss.Object, realStore.Status, NullLogger<StructureAudioStore>.Instance);
        }
        var service = new StructureAudioService(scope.ServiceProvider.GetRequiredService<IDocumentParseRepository>(),
            scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>(), repo.Object, store,
            NullLogger<StructureAudioService>.Instance);
        using var batch = new AudioImportBatch();
        var bytes = Mp3("safe");
        batch.Entries.Add(new("unit1/SectionA.mp3", new(".mp3", "audio/mpeg"), bytes.Length, () => new MemoryStream(bytes)));
        batch.Entries.Add(new("unit1/SectionB.mp3", new(".mp3", "audio/mpeg"), bytes.Length,
            () => failure == "read" ? throw new IOException("synthetic second read failure") : new MemoryStream(bytes)));
        if (failure.StartsWith("cancel-"))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(parseId, batch, new(), cancellation.Token));
        else
            (await service.ImportAsync(parseId, batch, new())).Status.Should().Be(failure == "parse-removed" ? AudioResultStatus.NotFound : AudioResultStatus.StorageUnavailable);

        var retained = failure is "cancel-after-commit" or "cancel-during-reload" or "unknown-commit" ? 2 : 0;
        (await CountAudioAsync(factory, parseText)).Should().Be(retained);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(failure == "unknown-reference-before-commit" ? 2 : retained);
        if (failure == "unknown-reference-before-commit")
        {
            var time = new ShiftedTime { Shift = TimeSpan.FromHours(2) };
            var recovery = new StructureAudioService(scope.ServiceProvider.GetRequiredService<IDocumentParseRepository>(),
                scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>(), actual, realStore,
                NullLogger<StructureAudioService>.Instance, time);
            (await recovery.SweepOrphansAsync()).Value!.Deleted.Should().Be(2);
            (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        }
        if (failure == "parse-removed") return;
        // Reload through the real API discovers committed writes, including a cancelled response.
        (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!["items"]!.AsArray().Should().HaveCount(retained);
    }
}
