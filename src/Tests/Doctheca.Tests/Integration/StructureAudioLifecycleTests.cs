using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Service.Audio;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>Persistence and object recovery only: real host, PostgreSQL and isolated S3 storage (#107).</summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class StructureAudioLifecycleTests(PostgreSqlFixture database, MinioFixture minio)
    : ServiceMantleIntegrationTestBase(database), IClassFixture<MinioFixture>
{
    private const string Sweep = "/admin/structure-audio/orphan-sweep";
    private readonly string _bucket = "audio-lifecycle-" + Guid.NewGuid().ToString("N")[..12];

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?>? extra = null,
        TimeProvider? time = null, bool failStorage = false)
    {
        var settings = minio.Settings(_bucket);
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return CreateFactory(settings: settings, configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            if (failStorage)
            {
                var failing = new Mock<IOssService>();
                // A provider exception can include private data; the store must discard its message.
                failing.Setup(store => store.DeleteAsync(It.IsAny<string>())).ThrowsAsync(new IOException("private-provider-detail"));
                failing.Setup(store => store.ListObjectsAsync(It.IsAny<string>())).ThrowsAsync(new IOException("private-provider-detail"));
                services.AddSingleton(failing.Object);
            }
            else services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
            if (time != null)
            {
                services.RemoveAll<StructureAudioService>();
                services.AddScoped(sp => ActivatorUtilities.CreateInstance<StructureAudioService>(sp, time));
            }
        });
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        return client;
    }

    [Fact]
    public async Task RecoveryRequiresAdmin_AndDeferredFeatureRoutesAreAbsent()
    {
        using var factory = Factory();
        using var anonymous = factory.CreateClient();
        using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));
        (await anonymous.PostAsync(Sweep, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await user.PostAsync(Sweep, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToList();
        routes.Should().Contain(Sweep);
        routes.Should().NotContain(route => route != Sweep && route != null &&
            (route.EndsWith("/audio") || route.Contains("playback-link") || route.Contains("/audio/{audioId")));
    }

    [Fact]
    public async Task StructureReplacementUnlinksRemovedNodes_AndKeepsRetainedLinksAndObjects()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId, nodes) = await SeedStructureAsync(factory, client);
        var kept = await StoreAsync(factory, parseId, nodes[0].Id);
        var removed = await StoreAsync(factory, parseId, nodes[1].Id);

        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>()
                .ReplaceAsync(parseId, [nodes[0]])).Should().BeTrue();

        using (var scope = factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>().GetByParseIdAsync(parseId);
            rows.Single(row => row.Id == kept.Id).NodeId.Should().Be(nodes[0].Id);
            var unlinked = rows.Single(row => row.Id == removed.Id);
            unlinked.NodeId.Should().BeNull();
            unlinked.MatchStatus.Should().Be(AudioMatchStatus.NodeRemoved);
            unlinked.LinkSource.Should().BeNull();
        }
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentTreeReplacementAndAudioInsertionCannotLeaveDanglingLinks()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId, nodes) = await SeedStructureAsync(factory, client);
        using var insertion = factory.Services.CreateScope();
        using var replacement = factory.Services.CreateScope();
        var audio = NewAudio(parseId, nodes[1].Id, $"audio/{parseId:N}/{Guid.NewGuid():N}.mp3");
        await Task.WhenAll(
            insertion.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>().InsertAsync(parseId, [audio]),
            replacement.ServiceProvider.GetRequiredService<IDocumentStructureRepository>().ReplaceAsync(parseId, [nodes[0]]));
        using var read = factory.Services.CreateScope();
        var rows = await read.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>().GetByParseIdAsync(parseId);
        rows.Should().ContainSingle().Which.NodeId.Should().BeNull();
        rows[0].MatchStatus.Should().Be(AudioMatchStatus.NodeRemoved);
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InvalidLinkBatchChangesNoRows_AndMissingNodesAreInsertedUnlinked()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId, nodes) = await SeedStructureAsync(factory, client);
        var first = await StoreAsync(factory, parseId, nodes[0].Id);
        var second = await StoreAsync(factory, parseId, Guid.NewGuid());
        using (var scope = factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>();
            (await repo.UpdateLinksAsync(parseId, [new(first.Id, null), new(second.Id, Guid.NewGuid())], [], DateTimeOffset.UtcNow))
                .Status.Should().Be(AudioLinkUpdateStatus.NodeNotFound);
            var rows = await repo.GetByParseIdAsync(parseId);
            rows.Single(row => row.Id == first.Id).NodeId.Should().Be(nodes[0].Id);
            rows.Single(row => row.Id == second.Id).MatchStatus.Should().Be(AudioMatchStatus.NodeRemoved);
        }
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ParseAndDocumentDeletionCleanEveryOwnedObjectIncludingInterruptedWrites()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (fileId, parseId, nodes) = await SeedStructureAsync(factory, client);
        await StoreAsync(factory, parseId, nodes[0].Id);
        var otherParse = await SeedParseAsync(factory, fileId);
        await StoreAsync(factory, otherParse, null);
        await minio.PutAsync(_bucket, $"audio/{parseId:N}/{Guid.NewGuid():N}.mp3", "interrupted"u8.ToArray());
        var legacy = "uploads/retained.pdf";
        await minio.PutAsync(_bucket, legacy, "legacy"u8.ToArray());

        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowsAsync(factory, parseId)).Should().BeEmpty();
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
        (await client.DeleteAsync($"/admin/document-files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowsAsync(factory, otherParse)).Should().BeEmpty();
        (await minio.KeysAsync(_bucket)).Should().Equal(legacy);
    }

    [Fact]
    public async Task SweepPreservesReferencedRecentAndLegacyObjects_ThenRecoversOnlyOldOrphans()
    {
        var time = new ShiftedTime();
        using var factory = Factory(time: time);
        using var client = Admin(factory);
        var (_, parseId, nodes) = await SeedStructureAsync(factory, client);
        var referenced = await StoreAsync(factory, parseId, nodes[0].Id);
        var orphan = $"audio/{Guid.NewGuid():N}/{Guid.NewGuid():N}.mp3";
        var legacy = "uploads/keep.pdf";
        await minio.PutAsync(_bucket, orphan, "orphan"u8.ToArray());
        await minio.PutAsync(_bucket, legacy, "legacy"u8.ToArray());
        var recent = await ReadData(await client.PostAsync(Sweep, null));
        recent!["deleted"]!.GetValue<int>().Should().Be(0);
        time.Shift = TimeSpan.FromHours(2);
        var recovered = await ReadData(await client.PostAsync(Sweep, null));
        recovered!["deleted"]!.GetValue<int>().Should().Be(1);
        recovered["failed"]!.GetValue<int>().Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEquivalentTo(referenced.ObjectKey, legacy);
        using var scope = factory.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<StructureAudioStore>().DeleteManyAsync([legacy]));
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("DocumentAudio:Enabled", "false", "DocumentAudio:Enabled")]
    [InlineData("DocumentAudio:Enabled", "invalid", "DocumentAudio settings")]
    [InlineData("Oss:BucketName", "", "Oss:BucketName")]
    [InlineData("Oss:InternalEndpoint", "", "Oss:InternalEndpoint")]
    public async Task DisabledOrMalformedStorageDoesNotBlockExistingFeatures(string key, string value, string reason)
    {
        using var factory = Factory(new() { [key] = value });
        using var client = Admin(factory);
        var (_, parseId, _) = await SeedStructureAsync(factory, client);
        var response = await client.PostAsync(Sweep, null);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED").And.Contain(reason);
        AssertSafe(body);
        using (var scope = factory.Services.CreateScope())
        using (var content = new MemoryStream("disabled-write"u8.ToArray()))
            await Assert.ThrowsAsync<AudioStorageException>(() => scope.ServiceProvider.GetRequiredService<StructureAudioStore>()
                .PutAsync(parseId, Guid.NewGuid(), ".mp3", content, "audio/mpeg"));
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        (await client.GetAsync("/admin/document-files")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/admin/document-parses/{parseId}/structure")).StatusCode.Should().Be(HttpStatusCode.OK);
        if (key != "Oss:InternalEndpoint")
            (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        else
        {
            // Legacy deletion already resolves its own S3 client on main. A missing global S3
            // endpoint does not redefine that baseline; remove only this fixture's database row.
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParses
                .Where(parse => parse.Id == parseId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task StorageFailureKeepsDeletionSuccessful_AndSweepRecoversAfterStorageReturns()
    {
        using var working = Factory();
        using var client = Admin(working);
        var (_, parseId, nodes) = await SeedStructureAsync(working, client);
        await StoreAsync(working, parseId, nodes[0].Id);
        using (var failing = Factory(failStorage: true))
        using (var failedClient = Admin(failing))
        {
            var deletion = await failedClient.DeleteAsync($"/admin/document-parses/{parseId}");
            deletion.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertSafe(await deletion.Content.ReadAsStringAsync());
            var sweep = await failedClient.PostAsync(Sweep, null);
            sweep.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            AssertSafe(await sweep.Content.ReadAsStringAsync());
        }
        (await RowsAsync(working, parseId)).Should().BeEmpty();
        (await minio.KeysAsync(_bucket)).Should().ContainSingle();
        using var recovered = Factory(time: new ShiftedTime { Shift = TimeSpan.FromHours(2) });
        using var recoveredClient = Admin(recovered);
        (await ReadData(await recoveredClient.PostAsync(Sweep, null)))!["deleted"]!.GetValue<int>().Should().Be(1);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    private void AssertSafe(string body)
    {
        body.Should().NotContain(_bucket).And.NotContain(minio.InternalEndpoint).And.NotContain(minio.PublicBaseUrl)
            .And.NotContain(minio.AccessKey).And.NotContain(minio.SecretKey).And.NotContain("private-provider-detail")
            .And.NotContain("objectKey");
    }

    private async Task<DocumentStructureAudioModel> StoreAsync(WebApplicationFactory<Program> factory, Guid parseId, Guid? nodeId)
    {
        using var scope = factory.Services.CreateScope();
        var id = Guid.NewGuid();
        using var content = new MemoryStream("synthetic-audio"u8.ToArray());
        var key = await scope.ServiceProvider.GetRequiredService<StructureAudioStore>().PutAsync(parseId, id, ".mp3", content, "audio/mpeg");
        var model = NewAudio(parseId, nodeId, key); model.Id = id;
        (await scope.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>().InsertAsync(parseId, [model])).Should().BeTrue();
        return model;
    }

    private static DocumentStructureAudioModel NewAudio(Guid parseId, Guid? nodeId, string key) => new()
    {
        Id = Guid.NewGuid(), ParseId = parseId, NodeId = nodeId, SourcePath = Guid.NewGuid().ToString("N") + ".mp3",
        ObjectKey = key, ContentType = "audio/mpeg", SizeBytes = 15, MatchStatus = AudioMatchStatus.Matched,
        LinkSource = AudioLinkSource.Prefill, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(Guid File, Guid Parse, List<DocumentStructureNodeModel> Nodes)> SeedStructureAsync(
        WebApplicationFactory<Program> factory, HttpClient client)
    {
        var level = Guid.NewGuid();
        var definition = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "Audio lifecycle " + Guid.NewGuid().ToString("N"), levels = new[] { new { id = level, name = "Level", parentLevelId = (Guid?)null } },
        })))!["id"]!.GetValue<Guid>();
        Guid file = Guid.NewGuid();
        var parse = await SeedParseAsync(factory, file, definition);
        var nodes = Enumerable.Range(0, 2).Select(i => new DocumentStructureNodeModel
        {
            Id = Guid.NewGuid(), ParseId = parse, LevelId = level, Name = "Node " + i,
            StartSequence = i, EndSequence = i, SortOrder = i, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IDocumentStructureRepository>().ReplaceAsync(parse, nodes)).Should().BeTrue();
        return (file, parse, nodes);
    }

    private static async Task<Guid> SeedParseAsync(WebApplicationFactory<Program> factory, Guid file, Guid? definition = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        if (!await context.DocumentFiles.AnyAsync(row => row.Id == file))
            context.DocumentFiles.Add(new DocumentFileEntity { Id = file, FileName = "lifecycle.pdf", ContentType = "application/pdf", StructureDefinitionId = definition });
        var parse = new DocumentParseEntity { Id = Guid.NewGuid(), DocumentFileId = file, Status = "parsed" };
        context.DocumentParses.Add(parse);
        context.DocumentParseBlocks.AddRange(Enumerable.Range(0, 2).Select(i => new DocumentParseBlockEntity
        {
            ParseId = parse.Id, PageId = 0, SortIndex = i, Sequence = i, BlockType = "text", TextContent = "block", TextFormat = "plain", BlockData = "{}",
        }));
        await context.SaveChangesAsync();
        return parse.Id;
    }

    private static async Task<List<DocumentStructureAudioModel>> RowsAsync(WebApplicationFactory<Program> factory, Guid parse)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentStructureAudioRepository>().GetByParseIdAsync(parse);
    }

    private static async Task<JsonNode?> ReadData(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonNode.Parse(body)!["data"];
    }

    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }
}
