using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minio.DataModel.Args;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class StructureAudioEndpointTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SourceFreeAudio58_PreviewThenOneAtomicExplicitSave_UsesRealCurrentReadersAndBytes(bool zip)
    {
        using var factory = Factory(extra: new() { ["DocumentAudio:MaxArchiveBytes"] = (4 * 1024 * 1024).ToString(), ["Oss:PublicBaseUrl"] = "" });
        using var client = Admin(factory); var book = await DocumentAudioFixture.Create(client);
        var root = $"/admin/document-files/{book.Id}/audio"; var prefix = zip ? "" : "generated-wave/";
        MultipartFormDataContent Files() => zip ? Upload(("official.zip", DocumentAudioFixture.Archive())) :
            Upload(DocumentAudioFixture.Paths.Select((p, i) => (prefix + p, DocumentAudioFixture.Wave(i))).ToArray());
        var preview = await DocumentAudioFixture.Data(await client.PostAsync(root + "?preview=true", Files()));
        preview["items"]!.AsArray().Count(n => n!["suggestedNodeId"] is not null && !n["sourcePath"]!.GetValue<string>().Contains("Vocabulary")).Should().Be(49);
        preview["items"]!.AsArray().Count(n => n!["suggestedNodeId"] is not null && n["sourcePath"]!.GetValue<string>().Contains("Vocabulary")).Should().Be(8);
        preview["items"]!.AsArray().Single(n => n!["suggestedNodeId"] is null)!["matchStatus"]!.GetValue<string>().Should().Be("folder_name_mismatch");
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            (await db.DocumentStructureAudio.CountAsync(a => a.DocumentFileId == book.Id)).Should().Be(0);
            (await db.DocumentParses.CountAsync(p => p.DocumentFileId == book.Id)).Should().Be(0);
        }
        using var files = Files();
        files.Add(new StringContent(JsonSerializer.Serialize(DocumentAudioFixture.Paths.Select(p => new { sourcePath = prefix + p, nodeId = DocumentAudioFixture.Target(book.Tree, p) }))), "targets");
        var saved = await DocumentAudioFixture.Data(await client.PostAsync(root, files)); saved["imported"]!.GetValue<int>().Should().Be(58);
        var rows = (await DocumentAudioFixture.Data(await client.GetAsync(root)))["items"]!.AsArray(); rows.Should().HaveCount(58);
        rows.Should().OnlyContain(a => a!["linkSource"]!.GetValue<string>() == "manual" && !a["requiresReview"]!.GetValue<bool>());
        using var dbScope = factory.Services.CreateScope(); var context = dbScope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var entities = await context.DocumentStructureAudio.AsNoTracking().Where(a => a.DocumentFileId == book.Id).ToListAsync();
        entities.Should().OnlyContain(a => a.ParseId == null && a.TreeId == Guid.Parse(book.Tree["treeId"]!.GetValue<string>()) && a.ObjectKey.StartsWith($"audio/documents/{book.Id:N}/"));
        using var storage = minio.Client(); using var anonymous = factory.CreateClient();
        var metadata = await DocumentAudioFixture.Data(await anonymous.GetAsync($"/internal/v1/documents/{book.Id}/structure"));
        var catalog = await DocumentAudioFixture.Data(await anonymous.GetAsync($"/internal/v1/documents?hasAudio=true&structureDefinitionId={book.Definition}"));
        catalog["items"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == book.Id.ToString())!["audioCount"]!.GetValue<int>().Should().Be(58);
        var direct = 0;
        foreach (var node in DocumentAudioFixture.Nodes(metadata))
        {
            foreach (var field in new[] { "startSequence", "endSequence", "startPageNumber", "endPageNumber" }) node[field].Should().BeNull();
            var id = node["id"]!.GetValue<string>();
            var list = await DocumentAudioFixture.Data(await anonymous.GetAsync($"/internal/v1/structure-nodes/{id}/audio"));
            var expected = entities.Where(a => a.NodeId == Guid.Parse(id)).ToList();
            list["items"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).Should().BeEquivalentTo(expected.Select(a => a.Id.ToString()));
            node["audioCount"]!.GetValue<int>().Should().Be(expected.Count); direct += expected.Count;
            foreach (var item in list["items"]!.AsArray())
            {
                var entity = expected.Single(a => a.Id.ToString() == item!["id"]!.GetValue<string>());
                var index = Array.IndexOf(DocumentAudioFixture.Paths, entity.SourcePath[prefix.Length..]);
                var bytes = DocumentAudioFixture.Wave(index);
                using var stored = new MemoryStream();
                await storage.GetObjectAsync(new GetObjectArgs().WithBucket(_bucket).WithObject(entity.ObjectKey).WithCallbackStream(stream => stream.CopyTo(stored)));
                SHA256.HashData(stored.ToArray()).Should().Equal(SHA256.HashData(bytes));
                var url = item!["playback"]!["url"]!.GetValue<string>(); // Capability stays in memory; no output or URL assertions with failure values.
                using var full = await anonymous.GetAsync(url); full.StatusCode.Should().Be(HttpStatusCode.OK);
                (await full.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
                foreach (var (range, status, part) in new[] { ("bytes=1-9", HttpStatusCode.PartialContent, bytes[1..10]), ("bytes=-8", HttpStatusCode.PartialContent, bytes[^8..]), ($"bytes={bytes.Length}-", HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>()) })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.TryAddWithoutValidation("Range", range);
                    using var response = await anonymous.SendAsync(request); response.StatusCode.Should().Be(status);
                    (await response.Content.ReadAsByteArrayAsync()).Should().Equal(part);
                }
            }
        }
        direct.Should().Be(58);
        var first = DocumentAudioFixture.Nodes(metadata).First()["id"]!.GetValue<string>();
        var content = await DocumentAudioFixture.Data(await anonymous.GetAsync($"/internal/v1/structure-nodes/{first}/content"));
        content["items"]!.AsArray().Should().BeEmpty(); content["hasMore"]!.GetValue<bool>().Should().BeFalse(); content["parseId"].Should().BeNull();
        (await anonymous.GetAsync($"/internal/v1/structure-nodes/{first}/images/{Guid.NewGuid()}/content?revision={content["revision"]!.GetValue<string>()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var search = await DocumentAudioFixture.Data(await anonymous.GetAsync($"/internal/v1/search?query=Paragraph&structureNodeId={first}"));
        search["results"]!.AsArray().Should().BeEmpty();
        (await context.DocumentParses.CountAsync(p => p.DocumentFileId == book.Id)).Should().Be(0);
        (await DocumentAudioFixture.Data(await client.GetAsync($"/admin/document-files/{book.Id}/structure")))["nodes"]!.ToJsonString().Should().Be(book.Tree["nodes"]!.ToJsonString());
        // Tightening the renamed definition retains every recording but rejects exactly the eight vocabulary targets.
        await SetAudioPolicy(client, book, true, true);
        var beforePolicySave = (await DocumentAudioFixture.Data(await client.GetAsync(root)))["items"]!;
        beforePolicySave.AsArray().Count(a => a!["requiresReview"]!.GetValue<bool>()).Should().Be(8);
        var invalidPolicySave = await client.PutAsJsonAsync(root, new { links = Array.Empty<object>() });
        invalidPolicySave.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(await invalidPolicySave.Content.ReadAsStringAsync())!["errors"]!.AsArray().Select(e => e!["file"]!.GetValue<string>())
            .Should().BeEquivalentTo(DocumentAudioFixture.Paths.Where(p => p.Contains("Vocabulary", StringComparison.Ordinal)).Select(p => prefix + p));
        (await DocumentAudioFixture.Data(await client.GetAsync(root)))["items"]!.ToJsonString().Should().Be(beforePolicySave.ToJsonString());
        var corrections = rows.Where(a => a!["sourcePath"]!.GetValue<string>().Contains("Vocabulary", StringComparison.Ordinal)).Select(a =>
        {
            var path = a!["sourcePath"]!.GetValue<string>()[prefix.Length..]; var unit = int.Parse(path[4..path.IndexOf('/')]);
            return new { id = a["id"]!.GetValue<string>(), nodeId = book.Tree["nodes"]![unit - 1]!["children"]![0]!["id"]!.GetValue<string>() };
        }).ToArray();
        await DocumentAudioFixture.Data(await client.PutAsJsonAsync(root, new { links = corrections }));
        var reviewed = (await DocumentAudioFixture.Data(await client.GetAsync(root)))["items"]!.AsArray();
        reviewed.Should().HaveCount(58).And.OnlyContain(a => !a!["requiresReview"]!.GetValue<bool>());
        reviewed.Select(a => a!["id"]!.GetValue<string>()).Should().BeEquivalentTo(rows.Select(a => a!["id"]!.GetValue<string>()));
        (await minio.KeysAsync(_bucket)).Should().BeEquivalentTo(entities.Select(a => a.ObjectKey));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RequiredPolicy_UsesWholeRetainedSet_MixedSaveIsAtomic_AndDeleteOnlyRecovers(bool legacy)
    {
        using var factory = Factory(); using var client = Admin(factory);
        var book = await DocumentAudioFixture.Create(client, required: false); var route = $"/admin/document-files/{book.Id}/audio";
        if (legacy)
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var p = new DocumentParseEntity { DocumentFileId = book.Id, Status = "parsed" }; db.DocumentParses.Add(p); await db.SaveChangesAsync();
            route = $"/admin/document-parses/{p.Id}/audio";
        }
        var existing = await DocumentAudioFixture.Data(await client.PostAsync(route, Upload(("one.mp3", Mp3("1")), ("two.mp3", Mp3("2")), ("three.mp3", Mp3("3")))));
        var rows = existing["items"]!.AsArray(); var one = rows[0]!["id"]!.GetValue<string>(); var two = rows[1]!["id"]!.GetValue<string>(); var three = rows[2]!["id"]!.GetValue<string>();
        await SetAudioPolicy(client, book, true, false);
        var baseline = (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.ToJsonString();
        (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.AsArray().Should().OnlyContain(a => a!["requiresReview"]!.GetValue<bool>());
        var keys = await minio.KeysAsync(_bucket);
        using var mixed = Upload(("new.mp3", Mp3("new")));
        mixed.Add(new StringContent(DocumentAudioFixture.Nodes(book.Tree).First()["id"]!.GetValue<string>()), "targetNodeId");
        mixed.Add(new StringContent(JsonSerializer.Serialize(new[] { new { id = one, nodeId = DocumentAudioFixture.Nodes(book.Tree).First()["id"]!.GetValue<string>() } })), "links");
        mixed.Add(new StringContent(JsonSerializer.Serialize(new[] { two })), "removeIds");
        var failed = await client.PostAsync(route, mixed); failed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var failure = JsonNode.Parse(await failed.Content.ReadAsStringAsync())!; failure["errorCode"]!.GetValue<string>().Should().Be("DOCTHECA_AUDIO_INVALID");
        failure["errors"]!.AsArray().Select(e => e!["file"]!.GetValue<string>()).Should().Equal(rows[2]!["sourcePath"]!.GetValue<string>());
        (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.ToJsonString().Should().Be(baseline);
        (await minio.KeysAsync(_bucket)).Should().BeEquivalentTo(keys);
        (await client.DeleteAsync(route + "/" + one)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
        var target = book.Tree["nodes"]![0]!["children"]![0]!["id"]!.GetValue<string>();
        using var complete = Upload(("new.mp3", Mp3("new")));
        complete.Add(new StringContent(target), "targetNodeId");
        complete.Add(new StringContent(JsonSerializer.Serialize(new[] { new { id = three, nodeId = target } })), "links");
        complete.Add(new StringContent(JsonSerializer.Serialize(new[] { two })), "removeIds");
        var fixedSave = await client.PostAsync(route, complete);
        fixedSave.StatusCode.Should().Be(HttpStatusCode.OK, await fixedSave.Content.ReadAsStringAsync());
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
        await SetAudioPolicy(client, book, true, true);
        var above = await client.PutAsJsonAsync(route, new { links = new[] { new { id = three, nodeId = book.Tree["nodes"]![0]!["id"]!.GetValue<string>() } } });
        above.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == three)!["nodeId"]!.GetValue<string>().Should().Be(target);
    }

    [Theory]
    [InlineData("GET", "/audio")] [InlineData("POST", "/audio")] [InlineData("PUT", "/audio")]
    [InlineData("DELETE", "/audio/00000000-0000-0000-0000-000000000002")]
    [InlineData("GET", "/audio/00000000-0000-0000-0000-000000000002/playback-link")]
    [InlineData("GET", "/structure/nodes/00000000-0000-0000-0000-000000000002/audio")]
    public async Task DocumentAudioRoutes_RequireAdminBeforeAnyBodyOrStorage(string method, string suffix)
    {
        using var factory = Factory(); using var anonymous = factory.CreateClient(); using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("user"));
        foreach (var (client, expected) in new[] { (anonymous, HttpStatusCode.Unauthorized), (user, HttpStatusCode.Forbidden) })
        {
            var response = await client.SendAsync(new(new(method), "/admin/document-files/00000000-0000-0000-0000-000000000001" + suffix));
            response.StatusCode.Should().Be(expected);
        }
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("absent", 400)] [InlineData("null-override", 400)] [InlineData("duplicate", 400)] [InlineData("unknown-path", 400)]
    [InlineData("bad-json", 400)] [InlineData("bad-id", 400)] [InlineData("foreign", 404)] [InlineData("history", 404)] [InlineData("common", 200)] [InlineData("normalized", 200)]
    public async Task ExplicitTargets_AreBoundedNormalizedOwnedAndConfirmed_WithNullOverridingCommon(string kind, int expected)
    {
        using var factory = Factory(); using var client = Admin(factory); var book = await DocumentAudioFixture.Create(client);
        var root = $"/admin/document-files/{book.Id}/audio"; var node = DocumentAudioFixture.Target(book.Tree, "unit1/SectionA.wav");
        using var form = Upload(("unit1/SectionA.wav", DocumentAudioFixture.Wave(0)));
        if (kind != "absent") form.Add(new StringContent(node), "targetNodeId");
        if (kind == "null-override") form.Add(new StringContent("[{\"sourcePath\":\"unit1/SectionA.wav\",\"nodeId\":null}]"), "targets");
        if (kind == "duplicate") form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { sourcePath = "unit1/SectionA.wav", nodeId = node }, new { sourcePath = "unit1\\SectionA.wav", nodeId = node } })), "targets");
        if (kind == "unknown-path") form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { sourcePath = "another.wav", nodeId = node } })), "targets");
        if (kind is "bad-json" or "bad-id") form.Add(new StringContent(kind == "bad-json" ? "[" : "[{\"sourcePath\":\"unit1/SectionA.wav\",\"nodeId\":\"bad\"}]"), "targets");
        if (kind == "foreign") { var other = await DocumentAudioFixture.Create(client); form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { sourcePath = "unit1/SectionA.wav", nodeId = DocumentAudioFixture.Target(other.Tree, "unit1/SectionA.wav") } })), "targets"); }
        if (kind == "history")
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var tree = new DocumentStructureTreeEntity { DocumentFileId = book.Id }; db.DocumentStructureTrees.Add(tree);
            var old = new DocumentStructureNodeEntity { DocumentFileId = book.Id, TreeId = tree.Id, LevelId = book.Unit, Name = "Historical" }; db.DocumentStructureNodes.Add(old); await db.SaveChangesAsync();
            form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { sourcePath = "unit1/SectionA.wav", nodeId = old.Id.ToString() } })), "targets");
        }
        if (kind == "normalized") form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { sourcePath = "unit1\\SectionA.wav", nodeId = node } })), "targets");
        var response = await client.PostAsync(root, form); ((int)response.StatusCode).Should().Be(expected, await response.Content.ReadAsStringAsync());
        (await minio.KeysAsync(_bucket)).Should().HaveCount(expected == 200 ? 1 : 0);
        var rows = (await DocumentAudioFixture.Data(await client.GetAsync(root)))["items"]!.AsArray(); rows.Should().HaveCount(expected == 200 ? 1 : 0);
        if (expected == 200) { rows[0]!["nodeId"]!.GetValue<string>().Should().Be(node); rows[0]!["linkSource"]!.GetValue<string>().Should().Be("manual"); }
    }

    [Fact]
    public async Task RequiredDescendantWithoutNode_AndHistoricalImport_CannotBeBypassedThroughParseAdapter()
    {
        using var factory = Factory(); using var client = Admin(factory); var book = await DocumentAudioFixture.Create(client, sections: false);
        await SetAudioPolicy(client, book, true, true);
        using var invalid = Upload(("vocabulary.wav", DocumentAudioFixture.Wave(0))); invalid.Add(new StringContent(book.Tree["nodes"]![0]!["id"]!.GetValue<string>()), "targetNodeId");
        (await client.PostAsync($"/admin/document-files/{book.Id}/audio", invalid)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var p = new DocumentParseEntity { DocumentFileId = book.Id, Status = "parsed" }; db.DocumentParses.Add(p);
        var history = new DocumentStructureTreeEntity { DocumentFileId = book.Id, OriginParseId = p.Id }; db.DocumentStructureTrees.Add(history);
        db.DocumentStructureNodes.Add(new() { TreeId = history.Id, DocumentFileId = book.Id, LevelId = book.Unit, Name = "History" }); await db.SaveChangesAsync();
        (await client.PostAsync($"/admin/document-parses/{p.Id}/audio?preview=true", Upload(("track.wav", DocumentAudioFixture.Wave(0))))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/admin/document-parses/{p.Id}/audio", Upload(("track.wav", DocumentAudioFixture.Wave(0))))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task MixedLegacyAndDocumentObjects_PreviewThroughDocumentRoute_AndSurviveParseDeletion()
    {
        using var factory = Factory(); using var client = Admin(factory); var book = await DocumentAudioFixture.Create(client);
        Guid parse;
        using (var scope = factory.Services.CreateScope())
        { var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>(); var p = new DocumentParseEntity { DocumentFileId = book.Id, Status = "parsed" }; db.DocumentParses.Add(p); await db.SaveChangesAsync(); parse = p.Id; }
        using var legacy = Upload(("legacy.wav", DocumentAudioFixture.Wave(0))); legacy.Add(new StringContent(book.Tree["nodes"]![0]!["id"]!.GetValue<string>()), "targetNodeId");
        await DocumentAudioFixture.Data(await client.PostAsync($"/admin/document-parses/{parse}/audio", legacy));
        using var current = Upload(("current.wav", DocumentAudioFixture.Wave(1))); current.Add(new StringContent(book.Tree["nodes"]![0]!["children"]![0]!["id"]!.GetValue<string>()), "targetNodeId");
        var route = $"/admin/document-files/{book.Id}/audio"; await DocumentAudioFixture.Data(await client.PostAsync(route, current));
        var list = (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.AsArray(); list.Should().HaveCount(2);
        using var download = new HttpClient();
        foreach (var deleteParse in new[] { false, true })
        {
            if (deleteParse) (await client.DeleteAsync($"/admin/document-parses/{parse}")).StatusCode.Should().Be(HttpStatusCode.OK);
            foreach (var item in list)
            {
                var link = await DocumentAudioFixture.Data(await client.GetAsync(route + "/" + item!["id"]!.GetValue<string>() + "/playback-link"));
                var bytes = await download.GetByteArrayAsync(link["url"]!.GetValue<string>());
                bytes.Should().Equal(DocumentAudioFixture.Wave(item["sourcePath"]!.GetValue<string>() == "legacy.wav" ? 0 : 1));
            }
        }
        (await DocumentAudioFixture.Data(await client.GetAsync(route)))["items"]!.ToJsonString().Should().Be(list.ToJsonString());
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);
    }

    private static async Task<JsonNode> SetAudioPolicy(HttpClient client, DocumentAudioFixture.Book book, bool required, bool child) =>
        await DocumentAudioFixture.Data(await client.PutAsJsonAsync($"/admin/structure-definitions/{book.Definition}", new { name = "Renamed definition", levels = new object[] {
            new { id = book.Unit, name = "Volume", parentLevelId = (Guid?)null, required }, new { id = book.Section, name = "Part", parentLevelId = (Guid?)book.Unit, required = child } } }));
}
