using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Repositories;
using Doctheca.Service.Audio;
using Doctheca.Tests.Structure;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Issue #90 end to end through the real host, PostgreSQL and an S3-compatible store: importing the
/// 58 official audio paths pre-links sections and vocabulary units and flags the folder/name
/// mismatch; links are changed, cleared and survive a reload; rejected imports store nothing;
/// removing nodes unlinks, deleting files, parses and documents leaves no stored object; no response carries an object key, bucket, endpoint or credential.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class StructureAudioEndpointTests(PostgreSqlFixture database, MinioFixture minio)
    : ServiceMantleIntegrationTestBase(database), IClassFixture<MinioFixture>
{
    private const long MaxFileBytes = 64 * 1024;
    private const long MaxArchiveBytes = 1024 * 1024;

    private readonly string _bucket = "audio-test-" + Guid.NewGuid().ToString("N")[..12];

    private WebApplicationFactory<Program> Factory(
        bool storage = true,
        Dictionary<string, string?>? extra = null,
        TimeProvider? audioTime = null, ILoggerProvider? logs = null)
    {
        var settings = storage ? minio.Settings(_bucket) : new Dictionary<string, string?> { ["Oss:InternalEndpoint"] = "" };
        settings["DocumentAudio:MaxFileBytes"] = MaxFileBytes.ToString();
        settings["DocumentAudio:MaxArchiveBytes"] = MaxArchiveBytes.ToString();
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return CreateFactory(configureTestServices: services =>
        {
            if (logs != null) services.AddSingleton(logs);
            if (storage)
            {
                // The real S3 client against the test container replaces the base double.
                services.RemoveAll<IOssService>();
                services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
            }
            if (audioTime != null)
            {
                services.RemoveAll<StructureAudioService>();
                services.AddScoped(sp => ActivatorUtilities.CreateInstance<StructureAudioService>(sp, audioTime));
            }
        }, settings: settings);
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));
        return client;
    }

    [Theory]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/audio")]
    [InlineData("POST", "/admin/document-parses/00000000-0000-0000-0000-000000000001/audio")]
    [InlineData("PUT", "/admin/document-parses/00000000-0000-0000-0000-000000000001/audio")]
    [InlineData("DELETE", "/admin/document-parses/00000000-0000-0000-0000-000000000001/audio/00000000-0000-0000-0000-000000000002")]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/structure/nodes/00000000-0000-0000-0000-000000000002/audio")]
    [InlineData("GET", "/admin/document-parses/00000000-0000-0000-0000-000000000001/audio/00000000-0000-0000-0000-000000000002/playback-link")]
    [InlineData("POST", "/admin/structure-audio/orphan-sweep")]
    public async Task AudioRoutes_RequireAnAdminSession(string method, string path)
    {
        using var factory = Factory();
        using var anonymous = factory.CreateClient();
        using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));

        var anonymousResponse = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        var userResponse = await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        if (path.EndsWith("/playback-link", StringComparison.Ordinal))
        {
            anonymousResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
            userResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task OfficialTextbookAudio_IsPreLinkedReviewedChangedAndProtected()
    {
        using var factory = Factory(extra: new());
        using var client = Admin(factory);
        var (fileId, parseId) = await SeedTextbookAsync(factory, client);

        // 1. The 58 files as one ZIP, with the usual macOS metadata next to them.
        var entries = StructureAudioMatcherTests.OfficialAudioPaths.Select(path => (path, Mp3(path))).ToList();
        entries.Add((".DS_Store", "junk"u8.ToArray()));
        entries.Add(("__MACOSX/unit1/._9s01Unit1SectionA1b.mp3", "junk"u8.ToArray()));
        var imported = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("official.zip", Zip(entries))));
        var importBody = await imported.Content.ReadAsStringAsync();
        imported.StatusCode.Should().Be(HttpStatusCode.OK, importBody);
        AssertNoStorageDetails(importBody);
        var data = JsonNode.Parse(importBody)!["data"]!;
        data["imported"]!.GetValue<int>().Should().Be(58);
        data["linked"]!.GetValue<int>().Should().Be(57);
        data["flagged"]!.GetValue<int>().Should().Be(1);
        data["skipped"]!.AsArray().Should().HaveCount(2);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(58).And.OnlyContain(key => key.StartsWith($"audio/{Guid.Parse(parseId):N}/"));

        var items = data["items"]!.AsArray();
        var mismatch = items.Single(item => item!["sourcePath"]!.GetValue<string>() == "unit3/9s04Unit4SectionA1e.mp3")!;
        mismatch["nodeId"].Should().BeNull();
        mismatch["matchStatus"]!.GetValue<string>().Should().Be("folder_name_mismatch");
        var vocabulary = items.Where(item => item!["fileName"]!.GetValue<string>().Contains("Vocabulary")).ToList();
        vocabulary.Should().HaveCount(8);
        vocabulary.Select(item => item!["nodePath"]!.GetValue<string>()).Should().OnlyContain(path => !path.Contains(" > "));
        var sections = items.Where(item => item!["nodeId"] != null && !item["fileName"]!.GetValue<string>().Contains("Vocabulary")).ToList();
        sections.Should().HaveCount(49);
        foreach (var item in sections)
        {
            var path = item!["sourcePath"]!.GetValue<string>();
            var unit = path[4..path.IndexOf('/')];
            item["nodePath"]!.GetValue<string>().Should().StartWith($"Unit {unit} ").And.EndWith(path.Contains("SectionA") ? " > Section A" : " > Section B");
            item["linkSource"]!.GetValue<string>().Should().Be("prefill");
        }

        Guid? directoryDefinition;
        using (var scope = factory.Services.CreateScope())
            directoryDefinition = await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentFiles
                .Where(file => file.Id == Guid.Parse(fileId)).Select(file => file.StructureDefinitionId).SingleAsync();
        async Task AssertDiscovery(int expected)
        {
            using var anonymous = factory.CreateClient();
            var catalog = (await ReadData(await anonymous.GetAsync($"/internal/v1/documents?hasAudio=true&structureDefinitionId={directoryDefinition}")))!;
            var document = catalog["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == fileId)!;
            document["audioCount"]!.GetValue<int>().Should().Be(expected);
            var metadata = (await ReadData(await anonymous.GetAsync($"/internal/v1/documents/{fileId}/structure")))!;
            AssertNoStorageDetails(metadata.ToJsonString());
            var directTotal = 0;
            foreach (var root in metadata["nodes"]!.AsArray())
            foreach (var node in new[] { root }.Concat(root!["children"]!.AsArray()))
            {
                var list = (await ReadData(await anonymous.GetAsync($"/internal/v1/structure-nodes/{node!["id"]}/audio")))!;
                var count = list["items"]!.AsArray().Count;
                node["audioCount"]!.GetValue<int>().Should().Be(count);
                directTotal += count;
            }
            directTotal.Should().Be(expected);
        }
        await AssertDiscovery(57);
        var tree = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/structure")))!;
        // 3. The administrator links the mismatch to Unit 4 Section A, clears another link, saves; a reload shows both.
        var unit4SectionA = tree["nodes"]![3]!["children"]![0]!["id"]!.GetValue<string>();
        var cleared = items.First(item => item!["sourcePath"]!.GetValue<string>() == "unit1/9s01Unit1SectionA1b.mp3")!["id"]!.GetValue<string>();
        var saved = await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new
        {
            links = new object[]
            {
                new { id = mismatch["id"]!.GetValue<string>(), nodeId = unit4SectionA },
                new { id = cleared, nodeId = (string?)null },
            },
        });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var reloaded = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!;
        AssertNoStorageDetails(reloaded.ToJsonString());
        var relinked = reloaded["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == mismatch["id"]!.GetValue<string>())!;
        relinked["nodeId"]!.GetValue<string>().Should().Be(unit4SectionA);
        relinked["linkSource"]!.GetValue<string>().Should().Be("manual");
        relinked["matchStatus"]!.GetValue<string>().Should().Be("folder_name_mismatch");
        var unlinked = reloaded["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == cleared)!;
        unlinked["nodeId"].Should().BeNull();
        unlinked["linkSource"]!.GetValue<string>().Should().Be("manual");
        reloaded["storage"]!["available"]!.GetValue<bool>().Should().BeTrue();
        reloaded["storage"]!["maxFileBytes"]!.GetValue<long>().Should().Be(MaxFileBytes);
        await AssertDiscovery(57);

        // 5. Removing a node in a structure save unlinks its audio; kept nodes keep theirs.
        var unit8 = tree["nodes"]![7]!.AsObject();
        var unit8SectionB = unit8["children"]![1]!["id"]!.GetValue<string>();
        var withoutUnit8SectionB = JsonNode.Parse(tree["nodes"]!.ToJsonString())!.AsArray();
        withoutUnit8SectionB[7]!["children"]!.AsArray().RemoveAt(1);
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes = withoutUnit8SectionB }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var afterSave = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!["items"]!.AsArray();
        var orphaned = afterSave.Single(item => item!["sourcePath"]!.GetValue<string>() == "unit8/9s06Unit8SectionB1b.mp3")!;
        orphaned["nodeId"].Should().BeNull();
        orphaned["matchStatus"]!.GetValue<string>().Should().Be("node_removed");
        afterSave.Count(item => item!["nodeId"] != null).Should().Be(56);
        await AssertDiscovery(56);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(58);

        // 6. Removing a file deletes its object; unknown files and nodes are 404 and change nothing.
        (await client.DeleteAsync($"/admin/document-parses/{parseId}/audio/{orphaned["id"]}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(57);
        await AssertDiscovery(56);
        var missing = Guid.NewGuid();
        (await client.DeleteAsync($"/admin/document-parses/{parseId}/audio/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new { links = new[] { new { id = missing, nodeId = (Guid?)null } } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new { links = new[] { new { id = Guid.Parse(cleared), nodeId = (Guid?)missing } } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/audio", new { removeIds = new[] { missing } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!["items"]!.AsArray().Should().HaveCount(57);

        // 7. The same paths again are a conflict; nothing is stored.
        var again = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("again.zip", Zip(entries.Take(3)))));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(57);

        // 8. Deleting the parse deletes every row and object, including one an interrupted import left.
        await minio.PutAsync(_bucket, $"audio/{Guid.Parse(parseId):N}/{Guid.NewGuid():N}.mp3", Mp3("left"));
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAudioAsync(factory, parseId)).Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        (await client.GetAsync($"/admin/document-parses/{parseId}/audio")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _ = fileId;
    }

    [Fact]
    public async Task DeletingTheDocumentFile_DeletesTheAudioOfAllItsParses()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (fileId, parseId) = await SeedTextbookAsync(factory, client);
        var files = Upload(("unit1/9s01Unit1SectionA1b.mp3", Mp3("a")), ("unit2/9sVocabulary02Unit2.mp3", Mp3("b")));
        (await client.PostAsync($"/admin/document-parses/{parseId}/audio", files)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(2);

        (await client.DeleteAsync($"/admin/document-files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CountAudioAsync(factory, parseId)).Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task SeveralFiles_KeepTheirFolders_FromFileNamesOrThePathsField()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);

        var byName = await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio",
            Upload(("unit2/9s06Unit2SectionB1b.mp3", Mp3("x")))));
        byName!["items"]![0]!["nodePath"]!.GetValue<string>().Should().EndWith(" > Section B");

        var form = Upload(("9s01Unit3SectionA1b.mp3", Mp3("y")));
        form.Add(new StringContent("官方音频/unit3/9s01Unit3SectionA1b.mp3"), "paths");
        var byPath = (await ReadData(await client.PostAsync($"/admin/document-parses/{parseId}/audio", form)))!["items"]![0]!;
        byPath["sourcePath"]!.GetValue<string>().Should().Be("官方音频/unit3/9s01Unit3SectionA1b.mp3");
        byPath["folder"]!.GetValue<string>().Should().Be("官方音频/unit3");
        byPath["nodePath"]!.GetValue<string>().Should().StartWith("Unit 3 ").And.EndWith(" > Section A");
    }

    [Fact]
    public async Task RejectedImports_StoreNothing()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        var good = ("unit1/9s01Unit1SectionA1b.mp3", Mp3("ok"));

        var rejected = new (string Name, HttpContent Content, HttpStatusCode Status, string Reason)[]
        {
            ("text file", Upload(good, ("unit1/notes.txt", "hello"u8.ToArray())), HttpStatusCode.BadRequest, "is not a supported audio file"),
            ("disguised file", Upload(good, ("unit1/fake.mp3", "<html>"u8.ToArray())), HttpStatusCode.BadRequest, "content does not match"),
            ("oversized file", Upload(good, ("unit1/big.mp3", Mp3("big", (int)MaxFileBytes + 1))), HttpStatusCode.BadRequest, "file limit"),
            ("oversized entry", Upload(("a.zip", Zip([good, ("unit1/big.mp3", Mp3("big", (int)MaxFileBytes + 1))]))), HttpStatusCode.BadRequest, "file limit"),
            ("parent path entry", Upload(("a.zip", Zip([good, ("../evil.mp3", Mp3("e"))]))), HttpStatusCode.BadRequest, "relative path segment"),
            ("absolute entry", Upload(("a.zip", Zip([good, ("/etc/evil.mp3", Mp3("e"))]))), HttpStatusCode.BadRequest, "absolute path"),
            ("drive entry", Upload(("a.zip", Zip([good, ("C:\\evil.mp3", Mp3("e"))]))), HttpStatusCode.BadRequest, "absolute path"),
            ("backslash parent entry", Upload(("a.zip", Zip([good, ("unit1\\..\\..\\evil.mp3", Mp3("e"))]))), HttpStatusCode.BadRequest, "relative path segment"),
            ("nested archive", Upload(("a.zip", Zip([good, ("inner.zip", Zip([good]))]))), HttpStatusCode.BadRequest, "is not a supported audio file"),
            ("not a zip", Upload(good, ("b.zip", "not a zip"u8.ToArray())), HttpStatusCode.BadRequest, "not a readable ZIP"),
            ("duplicate path", Upload(good, good), HttpStatusCode.BadRequest, "more than once"),
            ("only system files", Upload((".DS_Store", "x"u8.ToArray())), HttpStatusCode.BadRequest, "no audio files"),
            ("archive over the import limit", Upload(("a.zip", Zip(Enumerable.Range(0, 20).Select(i => ($"u/{i}.mp3", Mp3($"{i}", (int)MaxFileBytes - 1)))))), HttpStatusCode.RequestEntityTooLarge, "limit"),
        };
        foreach (var (name, content, status, reason) in rejected)
        {
            var response = await client.PostAsync($"/admin/document-parses/{parseId}/audio", content);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(status, name + ": " + body);
            body.Should().Contain(reason, name);
            AssertNoStorageDetails(body);
            (await minio.KeysAsync(_bucket)).Should().BeEmpty(name);
            (await CountAudioAsync(factory, parseId)).Should().Be(0, name);
        }

        // A request body beyond the import limit is refused before it is read.
        var huge = await client.PostAsync($"/admin/document-parses/{parseId}/audio",
            Upload(("huge.mp3", Mp3("h", (int)MaxArchiveBytes + 2 * 1024 * 1024))));
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
        (await CountAudioAsync(factory, parseId)).Should().Be(0);
    }

    [Fact]
    public async Task UnknownParse_ParseWithoutNodes_AndUnknownNode()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        var missing = Guid.NewGuid();
        var upload = Upload(("unit1/a.mp3", Mp3("a")));

        (await client.GetAsync($"/admin/document-parses/{missing}/audio")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/admin/document-parses/{missing}/audio", upload)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/admin/document-parses/{missing}/audio", new { links = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/admin/document-parses/{missing}/audio/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var (_, parseId) = await SeedParseAsync(factory, 10);
        var noNodes = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/a.mp3", Mp3("a"))));
        noNodes.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await noNodes.Content.ReadAsStringAsync()).Should().Contain("DOCTHECA_AUDIO_STRUCTURE_REQUIRED");
        (await minio.KeysAsync(_bucket)).Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutStorageConfiguration_ImportIsRefusedClearly_AndTheRestKeepsWorking()
    {
        using var factory = Factory(storage: false);
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);

        var refused = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/a.mp3", Mp3("a"))));
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        body["errorCode"]!.GetValue<string>().Should().Be("DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED");
        body["message"]!.GetValue<string>().Should().Contain("Oss:InternalEndpoint");

        var list = (await ReadData(await client.GetAsync($"/admin/document-parses/{parseId}/audio")))!;
        list["storage"]!["available"]!.GetValue<bool>().Should().BeFalse();
        list["items"]!.AsArray().Should().BeEmpty();
        (await client.GetAsync("/admin/document-files")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/admin/document-parses/{parseId}/structure")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"/admin/document-parses/{parseId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IncompleteStorageConfiguration_NamesTheMissingKeyWithoutValues()
    {
        using var factory = Factory(extra: new() { ["Oss:InternalEndpoint"] = "" });
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);

        var refused = await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/a.mp3", Mp3("a"))));
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await refused.Content.ReadAsStringAsync();
        body.Should().Contain("Oss:InternalEndpoint");
        AssertNoStorageDetails(body);
    }

    [Fact]
    public async Task OrphanSweep_DeletesOnlyUnreferencedObjectsPastTheGracePeriod()
    {
        var time = new ShiftedTime();
        using var factory = Factory(audioTime: time);
        using var client = Admin(factory);
        var (_, parseId) = await SeedTextbookAsync(factory, client);
        (await client.PostAsync($"/admin/document-parses/{parseId}/audio", Upload(("unit1/9s01Unit1SectionA1b.mp3", Mp3("k")))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var orphan = $"audio/{Guid.NewGuid():N}/{Guid.NewGuid():N}.mp3";
        await minio.PutAsync(_bucket, orphan, Mp3("o"));
        await minio.PutAsync(_bucket, "uploads/legacy.jpg", "legacy"u8.ToArray());

        // Within the grace period nothing is swept.
        (await ReadData(await client.PostAsync("/admin/structure-audio/orphan-sweep", null)))!["deleted"]!.GetValue<int>().Should().Be(0);
        (await minio.KeysAsync(_bucket)).Should().HaveCount(3);

        time.Shift = TimeSpan.FromHours(2);
        var swept = (await ReadData(await client.PostAsync("/admin/structure-audio/orphan-sweep", null)))!;
        swept["scanned"]!.GetValue<int>().Should().Be(2);
        swept["deleted"]!.GetValue<int>().Should().Be(1);
        var keys = await minio.KeysAsync(_bucket);
        keys.Should().HaveCount(2).And.Contain("uploads/legacy.jpg").And.NotContain(orphan);
    }

    // ---------- helpers ----------

    private void AssertNoStorageDetails(string body)
    {
        System.Text.RegularExpressions.Regex.IsMatch(body, "audio/[0-9a-f]{32}/").Should().BeFalse("object keys never leave the server");
        body.Should().NotContain(_bucket).And.NotContain(minio.AccessKey)
            .And.NotContain(minio.SecretKey).And.NotContain(minio.InternalEndpoint).And.NotContain($"localhost:{minio.Port}")
            .And.NotContain("objectKey", "the object key field is never projected");
    }

    /// <summary>A synthetic MP3 (ID3v2 header, then bytes derived from the seed); not real audio.</summary>
    private static byte[] Mp3(string seed, int size = 256)
    {
        var bytes = new byte[size];
        "ID3"u8.CopyTo(bytes);
        bytes[3] = 3;
        var hash = seed.GetHashCode(StringComparison.Ordinal);
        for (var i = 10; i < size; i++) bytes[i] = (byte)(hash >> (i % 4 * 8) ^ i);
        return bytes;
    }

    private static byte[] Zip(IEnumerable<(string Path, byte[] Content)> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static MultipartFormDataContent Upload(params (string Name, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files) form.Add(new ByteArrayContent(content), "files", name);
        return form;
    }

    private static async Task<JsonNode?> ReadData(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonNode.Parse(body)!["data"];
    }

    /// <summary>A parse with marked Unit / Section nodes named like the PEP Grade 9 textbook.</summary>
    private static async Task<(string FileId, string ParseId)> SeedTextbookAsync(WebApplicationFactory<Program> factory, HttpClient client)
    {
        Guid unit = Guid.NewGuid(), section = Guid.NewGuid();
        var definitionId = (await ReadData(await client.PostAsJsonAsync("/admin/structure-definitions", new
        {
            name = "Audio textbook " + Guid.NewGuid().ToString("N")[..6],
            levels = new object[] { new { id = unit, name = "Unit", parentLevelId = (Guid?)null }, new { id = section, name = "Section", parentLevelId = unit } },
        })))!["id"]!.GetValue<string>();
        var (fileId, parseId) = await SeedParseAsync(factory, 400);
        (await client.PutAsJsonAsync($"/admin/document-files/{fileId}/metadata", new { structureDefinitionId = definitionId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var titles = new[] { "The Changing World", "Saying Thank You", "Exploring the World", "Arts and Crafts", "Look Into Science!", "Sports and Health", "Making Our World a Better Place", "Media and You" };
        var nodes = Enumerable.Range(0, 8).Select(u => new
        {
            levelId = unit, name = $"Unit {u + 1} {titles[u]}", startSequence = u * 50, endSequence = u * 50 + 49,
            children = new object[]
            {
                new { levelId = section, name = "Section A", startSequence = u * 50, endSequence = u * 50 + 19, children = Array.Empty<object>() },
                new { levelId = section, name = "Section B", startSequence = u * 50 + 20, endSequence = u * 50 + 39, children = Array.Empty<object>() },
                new { levelId = section, name = "Project", startSequence = u * 50 + 40, endSequence = u * 50 + 49, children = Array.Empty<object>() },
            },
        }).ToArray();
        (await client.PutAsJsonAsync($"/admin/document-parses/{parseId}/structure", new { nodes })).StatusCode.Should().Be(HttpStatusCode.OK);
        return (fileId, parseId);
    }

    private static async Task<(string FileId, string ParseId)> SeedParseAsync(WebApplicationFactory<Program> factory, int blockCount)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var file = new DocumentFileEntity { Id = Guid.NewGuid(), FileName = $"book-{Guid.NewGuid():N}.pdf", ContentType = "application/pdf" };
        var parse = new DocumentParseEntity { Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = "parsed" };
        context.DocumentFiles.Add(file);
        context.DocumentParses.Add(parse);
        context.DocumentParseBlocks.AddRange(Enumerable.Range(0, blockCount).Select(i => new DocumentParseBlockEntity
        {
            ParseId = parse.Id, PageId = i / 10, SortIndex = i % 10, Sequence = i, BlockType = "text",
            TextContent = $"block {i}", TextFormat = "plain", BlockData = $"{{\"sequence\":{i}}}",
        }));
        await context.SaveChangesAsync();
        return (file.Id.ToString(), parse.Id.ToString());
    }

    private static async Task<int> CountAudioAsync(WebApplicationFactory<Program> factory, string parseId)
    {
        using var scope = factory.Services.CreateScope();
        var id = Guid.Parse(parseId);
        return await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureAudio.CountAsync(audio => audio.ParseId == id);
    }

    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }
}
