using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.StructaDoc;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class DocumentStructureEndpointTests
{
    private readonly Mock<IStructaDocClient> _authoringUpstream = new();
    private readonly Mock<IOssService> _authoringStorage = new();
    private WebApplicationFactory<Program> AuthoringFactory(bool configured = false) => CreateFactory(configureTestServices: services =>
    {
        services.RemoveAll<ISearchIndexService>(); services.AddSingleton<ISearchIndexService>(_searchIndex);
        services.RemoveAll<IStructaDocClient>(); services.AddSingleton(_authoringUpstream.Object);
        services.RemoveAll<IOssService>(); services.AddSingleton(_authoringStorage.Object);
    }, settings: configured ? new Dictionary<string, string?> { ["StructaDoc:BaseUrl"] = "http://structadoc.test", ["StructaDoc:ApiKey"] = "test-only" } : null);

    [Theory]
    [InlineData("POST", "/admin/document-files")]
    [InlineData("POST", "/admin/document-files/00000000-0000-0000-0000-000000000001/source")]
    [InlineData("GET", "/admin/document-files/00000000-0000-0000-0000-000000000001/structure")]
    [InlineData("PUT", "/admin/document-files/00000000-0000-0000-0000-000000000001/structure")]
    public async Task AuthoringRoutes_EnforceAdminBeforeAnySideEffects(string method, string path)
    {
        using var factory = AuthoringFactory(); using var anon = factory.CreateClient(); using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("user"));
        (await anon.SendAsync(new(new HttpMethod(method), path))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await user.SendAsync(new(new HttpMethod(method), path))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _authoringUpstream.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceFreeBook_HasExactMetadataNoParseAndNoExternalCalls_WithTruthfulPagedFilters()
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var (definition, unit, section) = await AuthoringDefinition(client);
        var book = await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "  PEP English Grade 9  ", subject = "English", grade = "9", year = "2026", structureDefinitionId = definition }));
        var id = book!["id"]!.GetValue<string>();
        book["fileName"]!.GetValue<string>().Should().Be("PEP English Grade 9");
        book["contentType"]!.GetValue<string>().Should().BeEmpty(); book["hasSource"]!.GetValue<bool>().Should().BeFalse();
        book["sourceState"]!.GetValue<string>().Should().Be("structure-only");
        var detail = (await ReadData(await client.GetAsync($"/admin/document-files/{id}")))!;
        detail["parses"]!.AsArray().Should().BeEmpty(); detail["subject"]!.GetValue<string>().Should().Be("English");
        detail["grade"]!.GetValue<string>().Should().Be("9"); detail["year"]!.GetValue<string>().Should().Be("2026");
        var empty = (await ReadData(await client.GetAsync($"/admin/document-files/{id}/structure")))!;
        empty["treeId"].Should().BeNull(); empty["nodes"]!.AsArray().Should().BeEmpty();
        var parse = await client.PostAsync($"/admin/document-files/{id}/parse", null);
        parse.StatusCode.Should().Be(HttpStatusCode.Conflict); (await parse.Content.ReadAsStringAsync()).Should().Contain("DOCTHECA_DOCUMENT_SOURCE_REQUIRED");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); var guid = Guid.Parse(id);
            var file = await db.DocumentFiles.SingleAsync(f => f.Id == guid); file.CreatedBy.Should().NotBeNull(); file.FilePath.Should().BeNull(); file.StructaDocDocumentId.Should().BeNull(); file.CurrentStructureTreeId.Should().BeNull();
            (await db.DocumentParses.AnyAsync(p => p.DocumentFileId == guid)).Should().BeFalse();
            await Assert.ThrowsAsync<DocumentSourceRequiredException>(() => new DocumentParseRepository(db).AddAsync(new() { DocumentFileId = guid }));
        }
        _authoringUpstream.Invocations.Should().BeEmpty(); _authoringStorage.Invocations.Should().BeEmpty(); _searchIndex.IndexedParses.Should().BeEmpty();
        var prefix = "page-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 5; i++) await client.PostAsJsonAsync("/admin/document-files", new { fileName = prefix + i });
        using (var scope = factory.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); db.DocumentFiles.Add(new() { FileName = prefix + "source", ContentType = "application/pdf", StructaDocDocumentId = Guid.NewGuid() }); await db.SaveChangesAsync(); }
        var pages = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var response = JsonNode.Parse(await client.GetStringAsync($"/admin/document-files?page={page}&pageSize=2&sourceState=structure-only&parseStatus=unparsed&fileName={prefix}"))!;
            response["total"]!.GetValue<int>().Should().Be(5);
            pages.AddRange(response["data"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
            response["data"]!.AsArray().Should().OnlyContain(n => n!["parseStatus"] == null && !n["hasSource"]!.GetValue<bool>());
        }
        pages.Should().OnlyHaveUniqueItems().And.HaveCount(5);
        var largePage = JsonNode.Parse(await client.GetStringAsync($"/admin/document-files?pageSize=500&sourceState=structure-only&fileName={prefix}"))!;
        largePage["pageSize"]!.GetValue<int>().Should().Be(500);
        largePage["data"]!.AsArray().Should().HaveCount(5);
        var sourced = JsonNode.Parse(await client.GetStringAsync($"/admin/document-files?sourceState=with-source&parseStatus=unparsed&fileName={prefix}"))!;
        sourced["total"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData("blank")] [InlineData("title")] [InlineData("subject")] [InlineData("grade")] [InlineData("year")] [InlineData("definition")] [InlineData("malformed")]
    public async Task InvalidCreate_ReportsFieldAndPersistsNothing(string mutation)
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var title = "reject-" + Guid.NewGuid().ToString("N");
        var body = new JsonObject { ["fileName"] = title };
        switch (mutation)
        {
            case "blank": body["fileName"] = " "; break;
            case "title": body["fileName"] = new string('x', 501); break;
            case "subject": body["subject"] = new string('x', 51); break;
            case "grade": body["grade"] = new string('x', 21); break;
            case "year": body["year"] = new string('x', 11); break;
            case "definition": body["structureDefinitionId"] = Guid.NewGuid().ToString(); break;
            default: body["structureDefinitionId"] = "invalid"; break;
        }
        var response = await client.PostAsJsonAsync("/admin/document-files", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest); (await response.Content.ReadAsStringAsync()).Should().Contain("errors");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        (await db.DocumentFiles.AnyAsync(f => f.FileName == body["fileName"]!.GetValue<string>())).Should().BeFalse();
        _authoringUpstream.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task DocumentTree_UsesArrayOrderAndStableIds_AndBindingDoesNotPromoteHistoricalTree()
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var (definition, unit, section) = await AuthoringDefinition(client);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "PEP English Grade 9", structureDefinitionId = definition })))!["id"]!.GetValue<string>();
        var first = new JsonArray(Enumerable.Range(1, 14).Select(i => (JsonNode)DraftNode(unit, "Unit " + i, DraftNode(section, "Section A"))).ToArray());
        var saved = (await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes = first })))!;
        var treeId = saved["treeId"]!.GetValue<string>(); var nodes = saved["nodes"]!.AsArray();
        var ids = nodes.Select(n => n!["id"]!.GetValue<string>()).ToArray();
        // Move a sibling, rename one, remove a subtree, then preserve the exact result across binding and reparse.
        var moved = nodes[13]!.DeepClone(); nodes.RemoveAt(13); nodes.Insert(0, moved); nodes[1]!["name"] = "Renamed";
        nodes.RemoveAt(3);
        await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes }));
        Guid audioId;
        string audioBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var audio = new DocumentStructureAudioEntity { DocumentFileId = Guid.Parse(id), TreeId = Guid.Parse(treeId), NodeId = Guid.Parse(nodes[0]!["id"]!.GetValue<string>()), SourcePath = "preserved.wav", ObjectKey = "audio/preserved-authoring.wav", ContentType = "audio/wav", SizeBytes = 12 };
            db.DocumentStructureAudio.Add(audio); await db.SaveChangesAsync(); audioId = audio.Id; audioBefore = System.Text.Json.JsonSerializer.Serialize(audio);
        }
        var source = Guid.NewGuid(); Stream? uploadedStream = null;
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), "application/pdf", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Stream, CancellationToken>((_, _, s, _) => uploadedStream = s).ReturnsAsync(new StructaDocDocumentResponse { Id = source });
        (await client.PostAsync($"/admin/document-files/{id}/source", Pdf())).StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.Throws<ObjectDisposedException>(() => uploadedStream!.ReadByte());
        var attached = (await ReadData(await client.GetAsync($"/admin/document-files/{id}")))!;
        attached["fileName"]!.GetValue<string>().Should().Be("PEP English Grade 9"); attached["parses"]!.AsArray().Should().BeEmpty();
        var parsed = (await ReadData(await client.PostAsync($"/admin/document-files/{id}/parse", null)))!["parseId"]!.GetValue<string>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); var parseId = Guid.Parse(parsed);
            await new DocumentParseRepository(db).CompleteAsync(parseId, Guid.NewGuid(), "body", [], [], Enumerable.Range(0, 200).Select(i => new DocumentParseBlockModel { ParseId = parseId, Sequence = i, BlockType = "text", TextContent = "paragraph", BlockData = "{}" }).ToArray());
            var historical = new DocumentStructureTreeEntity { DocumentFileId = Guid.Parse(id), OriginParseId = parseId }; db.DocumentStructureTrees.Add(historical); await db.SaveChangesAsync();
            db.DocumentStructureNodes.Add(new() { TreeId = historical.Id, DocumentFileId = Guid.Parse(id), ParseId = parseId, LevelId = unit, Name = "Historical", StartSequence = 0, EndSequence = 199 }); await db.SaveChangesAsync();
        }
        for (var i = 0; i < nodes.Count; i++) { nodes[i]!["parseId"] = parsed; nodes[i]!["startSequence"] = (nodes.Count - 1 - i) * 10; nodes[i]!["endSequence"] = (nodes.Count - 1 - i) * 10 + 9; }
        var bound = (await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes })))!;
        bound["treeId"]!.GetValue<string>().Should().Be(treeId);
        bound["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).Should().Equal(nodes.Select(n => n!["id"]!.GetValue<string>()));
        using (var scope = factory.Services.CreateScope())
        { var repo = scope.ServiceProvider.GetRequiredService<IDocumentAuthoringRepository>(); (await repo.GetCurrentBindingsAsync(Guid.Parse(parsed))).Select(n => n.Id.ToString()).Should().BeEquivalentTo(nodes.Select(n => n!["id"]!.GetValue<string>())); }
        var history = (await ReadData(await client.GetAsync($"/admin/document-parses/{parsed}/structure")))!;
        history["nodes"]!.AsArray().Single()!["name"]!.GetValue<string>().Should().Be("Historical");
        foreach (var n in nodes) { n!["parseId"] = null; n["startSequence"] = null; n["endSequence"] = null; }
        await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes }));
        _searchIndex.IndexedParses.Should().Contain(Guid.Parse(parsed));
        var reload = (await ReadData(await client.GetAsync($"/admin/document-files/{id}/structure")))!;
        reload["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).Should().Equal(nodes.Select(n => n!["id"]!.GetValue<string>()));
        for (var i = 0; i < nodes.Count; i++) { nodes[i]!["parseId"] = parsed; nodes[i]!["startSequence"] = (nodes.Count - 1 - i) * 10; nodes[i]!["endSequence"] = (nodes.Count - 1 - i) * 10 + 9; }
        await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes }));
        var reparse = (await ReadData(await client.PostAsync($"/admin/document-files/{id}/parse", null)))!["parseId"]!.GetValue<string>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            await new DocumentParseRepository(db).CompleteAsync(Guid.Parse(reparse), Guid.NewGuid(), "new result", [], [], []);
            var audioAfter = await db.DocumentStructureAudio.AsNoTracking().SingleAsync(a => a.Id == audioId);
            System.Text.Json.JsonSerializer.Serialize(audioAfter).Should().Be(audioBefore);
        }
        var reparsed = (await ReadData(await client.GetAsync($"/admin/document-files/{id}/structure")))!;
        reparsed["treeId"]!.GetValue<string>().Should().Be(treeId);
        reparsed["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).Should().Equal(nodes.Select(n => n!["id"]!.GetValue<string>()));
        reparsed["nodes"]!.AsArray().Should().OnlyContain(n => n!["parseId"] == null && n["startSequence"] == null && n["endSequence"] == null);
        (await client.PutAsJsonAsync($"/admin/document-files/{id}/metadata", new { subject = "must not commit", structureDefinitionId = "" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadData(await client.GetAsync($"/admin/document-files/{id}")))!["subject"].Should().BeNull();
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("level")] [InlineData("blank")] [InlineData("long")]
    [InlineData("partial")] [InlineData("missing-parse")] [InlineData("reversed")] [InlineData("endpoint")]
    [InlineData("unbound-parent")] [InlineData("outside")] [InlineData("overlap")] [InlineData("mixed")]
    [InlineData("foreign-id")] [InlineData("history-id")] [InlineData("parent-level")] [InlineData("foreign-parse")] [InlineData("pending-parse")] [InlineData("too-many")]
    public async Task InvalidDocumentTree_IsAtomic(string mutation)
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var (definition, unit, section) = await AuthoringDefinition(client);
        var (file, parse) = await SeedDocumentAsync(factory, 20);
        await client.PutAsJsonAsync($"/admin/document-files/{file}/metadata", new { structureDefinitionId = definition });
        var original = (await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{file}/structure", new { nodes = new[] { DraftNode(unit, "Original") } })))!;
        var nodes = new JsonArray(DraftNode(unit, "Changed")); var root = nodes[0]!;
        void Bind(JsonNode n, string p, int start, int end) { n["parseId"] = p; n["startSequence"] = start; n["endSequence"] = end; }
        switch (mutation)
        {
            case "duplicate": root["id"] = Guid.NewGuid().ToString(); nodes.Add(root.DeepClone()); break;
            case "level": root["levelId"] = section.ToString(); break;
            case "blank": root["name"] = " "; break;
            case "long": root["name"] = new string('x', 501); break;
            case "partial": root["startSequence"] = 0; break;
            case "missing-parse": Bind(root, Guid.NewGuid().ToString(), 0, 1); break;
            case "reversed": Bind(root, parse, 4, 2); break;
            case "endpoint": Bind(root, parse, 0, 99); break;
            case "unbound-parent": var child = DraftNode(section, "Child"); Bind(child, parse, 0, 1); root["children"]!.AsArray().Add(child); break;
            case "outside": Bind(root, parse, 0, 1); var outside = DraftNode(section, "Child"); Bind(outside, parse, 0, 2); root["children"]!.AsArray().Add(outside); break;
            case "overlap": Bind(root, parse, 0, 5); var second = DraftNode(unit, "Second"); Bind(second, parse, 5, 10); nodes.Add(second); break;
            case "mixed": Bind(root, parse, 0, 1); var mixed = DraftNode(unit, "Second"); Bind(mixed, Guid.NewGuid().ToString(), 2, 3); nodes.Add(mixed); break;
            case "foreign-id":
                var (other, otherParse) = await SeedDocumentAsync(factory, 5); await client.PutAsJsonAsync($"/admin/document-files/{other}/metadata", new { structureDefinitionId = definition });
                var otherTree = (await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{other}/structure", new { nodes = new[] { DraftNode(unit, "Other") } })))!;
                root["id"] = otherTree["nodes"]![0]!["id"]!.DeepClone(); break;
            case "history-id":
                using (var scope = factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); var history = new DocumentStructureTreeEntity { DocumentFileId = Guid.Parse(file), OriginParseId = Guid.Parse(parse) }; db.DocumentStructureTrees.Add(history); await db.SaveChangesAsync(); var h = new DocumentStructureNodeEntity { TreeId = history.Id, DocumentFileId = Guid.Parse(file), LevelId = unit, Name = "Historical" }; db.DocumentStructureNodes.Add(h); await db.SaveChangesAsync(); root["id"] = h.Id.ToString(); } break;
            case "parent-level": root["children"]!.AsArray().Add(DraftNode(unit, "Wrong level")); break;
            case "foreign-parse": var (_, foreignParse) = await SeedDocumentAsync(factory, 5); Bind(root, foreignParse, 0, 1); break;
            case "pending-parse": using (var scope = factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); await db.DocumentParses.Where(p => p.Id == Guid.Parse(parse)).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "pending")); } Bind(root, parse, 0, 1); break;
            case "too-many": for (var i = 0; i < 5000; i++) nodes.Add(DraftNode(unit, "Many")); break;
        }
        (await client.PutAsJsonAsync($"/admin/document-files/{file}/structure", new { nodes })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = (await ReadData(await client.GetAsync($"/admin/document-files/{file}/structure")))!;
        after.ToJsonString().Should().Be(original.ToJsonString());
    }

    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("unsupported")] [InlineData("malformed")] [InlineData("unconfigured")]
    public async Task SourceAttachment_RejectsWithoutUpload(string scenario)
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var book = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Source checks" })))!;
        var id = scenario == "missing" ? Guid.NewGuid().ToString() : book["id"]!.GetValue<string>();
        HttpContent content = scenario switch { "malformed" => new StringContent("invalid"), "empty" => new MultipartFormDataContent(), "unsupported" => Pdf("text/plain"), _ => Pdf() };
        (await client.PostAsync($"/admin/document-files/{id}/source", content)).StatusCode.Should().Be(scenario == "missing" ? HttpStatusCode.NotFound : scenario == "unconfigured" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest);
        _authoringUpstream.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentAttachments_HaveOneWinnerAndOnlyCleanTheLosingRemoteSource()
    {
        using var factory = AuthoringFactory(true); using var client = Admin(factory);
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Retained title", subject = "English" })))!["id"]!.GetValue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = Guid.NewGuid(); var winner = Guid.NewGuid(); var calls = 0;
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Stream _, CancellationToken ct) => { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await resume.Task.WaitAsync(ct); return new() { Id = loser }; } return new() { Id = winner }; });
        _authoringUpstream.Setup(u => u.DeleteDocumentAsync(loser, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var late = client.PostAsync($"/admin/document-files/{id}/source", Pdf()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { (await client.PostAsync($"/admin/document-files/{id}/source", Pdf())).StatusCode.Should().Be(HttpStatusCode.OK); }
        finally { resume.TrySetResult(); }
        (await late).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/admin/document-files/{id}/source", Pdf())).StatusCode.Should().Be(HttpStatusCode.Conflict);
        _authoringUpstream.Verify(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _authoringUpstream.Verify(u => u.DeleteDocumentAsync(loser, It.IsAny<CancellationToken>()), Times.Once);
        _authoringUpstream.Verify(u => u.DeleteDocumentAsync(winner, It.IsAny<CancellationToken>()), Times.Never);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var book = await db.DocumentFiles.SingleAsync(f => f.Id == Guid.Parse(id)); book.StructaDocDocumentId.Should().Be(winner); book.FileName.Should().Be("Retained title"); book.Subject.Should().Be("English");
    }

    [Fact]
    public async Task MissingLevelId_IsRejectedEvenWhenAnExistingLevelHasTheEmptyUuid()
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var definition = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Existing UUID level", levels = new[] { new { id = Guid.Empty, name = "Configured level", parentLevelId = (Guid?)null } } })))!["id"]!.GetValue<string>();
        // Simulate a preserved configured UUID from existing storage; EF generates a UUID for a new empty-key entity.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().StructureLevels.Where(l => l.DefinitionId == Guid.Parse(definition))
                .ExecuteUpdateAsync(set => set.SetProperty(l => l.Id, Guid.Empty));
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Required level", structureDefinitionId = definition })))!["id"]!.GetValue<string>();
        var invalid = await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes = new[] { new { name = "Missing level", children = Array.Empty<object>() } } });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("nodes[0].levelId");
        (await ReadData(await client.GetAsync($"/admin/document-files/{id}/structure")))!["treeId"].Should().BeNull();
        // This is a real configured level id, never a fabricated parse identity.
        (await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes = new[] { DraftNode(Guid.Empty, "Explicit level") } })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DocumentTree_SupportsEveryConfiguredLevelWithoutFixedNamesOrJsonDepthFailure()
    {
        using var factory = AuthoringFactory(); using var client = Admin(factory);
        var levels = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        var definition = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Fifty levels", levels = levels.Select((id, i) => new { id, name = "L" + i, parentLevelId = i == 0 ? (Guid?)null : levels[i - 1] }) })))!["id"]!.GetValue<string>();
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Deep book", structureDefinitionId = definition })))!["id"]!.GetValue<string>();
        var root = DraftNode(levels[49], "Level 50");
        for (var i = 48; i >= 0; i--) root = DraftNode(levels[i], "Level " + (i + 1), root);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { MaxDepth = 128 };
        var saved = await client.PutAsync($"/admin/document-files/{id}/structure", JsonContent.Create(new { nodes = new[] { root } }, options: options));
        saved.StatusCode.Should().Be(HttpStatusCode.OK); (await saved.Content.ReadAsStringAsync()).Should().Contain("Level 50");
        var reloaded = await client.GetAsync($"/admin/document-files/{id}/structure"); reloaded.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reloaded.Content.ReadAsStringAsync()).Should().Contain("Level 50");
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureNodes.CountAsync(n => n.DocumentFileId == Guid.Parse(id))).Should().Be(50);
    }

    private static JsonObject DraftNode(Guid level, string name, params JsonObject[] children) => new() { ["levelId"] = level.ToString(), ["name"] = name, ["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray()) };
    private static MultipartFormDataContent Pdf(string contentType = "application/pdf") { var form = new MultipartFormDataContent(); var bytes = new ByteArrayContent("%PDF-1.7 test"u8.ToArray()); bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType); form.Add(bytes, "file", "source.pdf"); return form; }
    private static async Task<(string Definition, Guid Unit, Guid Section)> AuthoringDefinition(HttpClient client)
    {
        var unit = Guid.NewGuid(); var section = Guid.NewGuid();
        var definition = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Authoring " + unit, levels = new object[] { new { id = unit, name = "Unit", parentLevelId = (Guid?)null }, new { id = section, name = "Section", parentLevelId = unit } } })))!["id"]!.GetValue<string>();
        return (definition, unit, section);
    }
}
