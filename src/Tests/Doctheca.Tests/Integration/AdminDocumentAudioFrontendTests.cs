using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service;
using Doctheca.Service.StructaDoc;
using Doctheca.Tests.OpenSearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    [Fact]
    public async Task SourceFreeBook58_OneConsoleSave_AllInternalUrlsReallyPlay_ThenSourceBindingAndReparseKeepEveryRecording()
    {
        var search = new OpenSearchFixture(); await search.InitializeAsync();
        try
        {
            var source = Guid.NewGuid(); var upstream = new Mock<IStructaDocClient>();
            upstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocDocumentResponse { Id = source });
            upstream.Setup(u => u.CreateParseRunAsync(source, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new() { Id = Guid.NewGuid(), DocumentId = source, Status = "succeeded" });
            upstream.Setup(u => u.GetParseRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => new() { Id = id, DocumentId = source, Status = "succeeded" });
            upstream.Setup(u => u.GetAllBlocksAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Range(0, 40).Select(i => new StructaDocBlockResponse { Id = Guid.NewGuid(), Sequence = i, PageNumber = 1, Type = "text", Content = "audio binding beacon " + i }).ToList());
            upstream.Setup(u => u.GetPagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            upstream.Setup(u => u.GetAssetsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            upstream.Setup(u => u.GetMarkdownAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("audio binding beacon");
            var bucket = "book-audio-" + Guid.NewGuid().ToString("N")[..12]; var index = "book-audio-" + Guid.NewGuid().ToString("N");
            var settings = minio.Settings(bucket); settings["Oss:PublicBaseUrl"] = "";
            settings["StructaDoc:BaseUrl"] = "http://structadoc.test"; settings["StructaDoc:ApiKey"] = "test-only";
            settings["OpenSearch:Url"] = search.Url; settings["OpenSearch:IndexName"] = index;
            await using var h = await Open(settings: settings, configureServices: services =>
            {
                services.RemoveAll<IOssService>(); services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
                services.RemoveAll<IStructaDocClient>(); services.AddSingleton(upstream.Object);
                services.RemoveAll<ISearchIndexService>(); services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
            });
            await h.Factory.Services.GetRequiredService<ISearchIndexService>().EnsureIndexAsync();
            await h.Page.GotoAsync(h.Origin + "/#docs");
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
            await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "新建书籍", Exact = true })).ToBeVisibleAsync();
            var cookies = new System.Net.CookieContainer();
            foreach (var cookie in await h.Context.CookiesAsync()) cookies.Add(new Uri(h.Origin), new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path));
            using var api = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(h.Origin) };
            var csrf = JsonNode.Parse(await api.GetStringAsync("/admin/auth/oidc/csrf"))!["token"]!.GetValue<string>();
            api.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
            // Configuration and the current tree use the actual merged authoring APIs; the logical book is created in the real console.
            var u = Guid.NewGuid(); var s = Guid.NewGuid();
            var definition = await DocumentAudioFixture.Data(await api.PostAsJsonAsync("/admin/structure-definitions", new { name = "Listening " + Guid.NewGuid(), levels = new object[] {
                new { id = u, name = "Unit", required = true, parentLevelId = (Guid?)null }, new { id = s, name = "Section", required = false, parentLevelId = (Guid?)u } } }));
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "新建书籍", Exact = true }).ClickAsync();
            await h.Page.GetByLabel("书名", new() { Exact = true }).FillAsync("PEP English Grade 9");
            await h.Page.GetByLabel("学科", new() { Exact = true }).FillAsync("English"); await h.Page.GetByLabel("年级", new() { Exact = true }).FillAsync("9");
            await h.Page.GetByLabel("书籍结构定义").SelectOptionAsync(definition["id"]!.GetValue<string>());
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存书籍", Exact = true }).ClickAsync();
            await h.Page.WaitForFunctionAsync("location.hash.startsWith('#detail/')");
            var file = Guid.Parse((await h.Page.EvaluateAsync<string>("location.hash")).Split('/')[1]);
            var tree = await DocumentAudioFixture.Data(await api.PutAsJsonAsync($"/admin/document-files/{file}/structure", new { nodes = Enumerable.Range(1, 14).Select(i => new {
                levelId = u, name = "Unit " + i, children = new[] { new { levelId = s, name = "Section A" }, new { levelId = s, name = "Section B" } } }) }));
            var nodeIdentity = TreeIdentity(tree);
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "管理书籍音频", Exact = true }).ClickAsync();
            await h.Page.WaitForFunctionAsync("location.hash.startsWith('#document-audio/')");
            await Assertions.Expect(h.Page.GetByTestId("audio-policy")).ToContainTextAsync("Unit：必需；Section：可选");
            Assert.Equal(new[] { 403, 403, 403 }, await h.Page.EvaluateAsync<int[]>("""
                async id => {
                  const route = '/admin/document-files/' + id + '/audio';
                  const preview = await fetch(route + '?preview=true', { method: 'POST', body: new FormData() });
                  const save = await fetch(route, { method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': 'invalid' }, body: JSON.stringify({ links: [] }) });
                  const remove = await fetch(route + '/00000000-0000-0000-0000-000000000001', { method: 'DELETE' });
                  return [preview.status, save.status, remove.status];
                }
                """, file.ToString()));
            var folder = Path.Combine(h.Root, "generated-wave");
            for (var i = 0; i < DocumentAudioFixture.Paths.Length; i++)
            { var target = Path.Combine(folder, DocumentAudioFixture.Paths[i]); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, DocumentAudioFixture.Wave(i)); }
            await h.Page.GetByTestId("audio-folder-input").SetInputFilesAsync(folder);
            await Assertions.Expect(h.Page.GetByText("已选择 58 个文件，确认目标后点击保存")).ToBeVisibleAsync();
            await Assertions.Expect(h.Page.Locator("tbody tr")).ToHaveCountAsync(58);
            Assert.Empty(await minio.KeysAsync(bucket)); Assert.Equal(0, await ParseCount()); Assert.Empty(await AudioRows());
            upstream.Verify(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
            var mismatch = h.Page.Locator("tr[data-audio-path='generated-wave/unit3/9s04Unit4SectionA1e.wav']");
            await Assertions.Expect(mismatch).ToContainTextAsync("文件夹与文件名不一致");
            await mismatch.Locator(".el-select").ClickAsync();
            await h.Page.Locator(".el-select-dropdown__item:visible", new() { HasText = "Unit 4 > Section A" }).ClickAsync();
            var save = await h.Page.RunAndWaitForResponseAsync(() => h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync(),
                response => response.Request.Method == "POST" && response.Url.EndsWith($"/admin/document-files/{file}/audio", StringComparison.Ordinal));
            Assert.Equal(200, save.Status);
            await Assertions.Expect(h.Page.GetByText("音频关联已保存", new() { Exact = true })).ToBeVisibleAsync();
            await h.Page.ReloadAsync(); await Assertions.Expect(h.Page.Locator("tbody tr")).ToHaveCountAsync(58);
            await Assertions.Expect(mismatch).ToContainTextAsync("已手动关联"); Assert.Equal(0, await ParseCount());
            var before = await AudioRows(); Assert.Equal(58, before.Count); Assert.All(before, a => { Assert.Null(a.ParseId); Assert.Equal("manual", a.LinkSource); });
            Assert.Equal(58, before.Select(a => Convert.ToHexString(SHA256.HashData(DocumentAudioFixture.Wave(Array.IndexOf(DocumentAudioFixture.Paths, a.SourcePath["generated-wave/".Length..]))))).Distinct().Count());
            var audioIdentity = JsonSerializer.Serialize(before);
            await VerifyAndPlayAll(before);
            // Attach a valid single-page PDF to this same book. Upload alone does not enqueue a parse.
            await h.Page.GotoAsync(h.Origin + "/#detail/" + file);
            await h.Page.GetByLabel("附加首个源文件", new() { Exact = true }).SetInputFilesAsync(new FilePayload { Name = "source.pdf", MimeType = "application/pdf", Buffer = MinimalAudioPdf() });
            await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true })).ToBeVisibleAsync();
            Assert.Equal(0, await ParseCount()); Assert.Equal(audioIdentity, JsonSerializer.Serialize(await AudioRows()));
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true }).ClickAsync();
            var parse = await WaitParsed(1);
            Assert.Equal(nodeIdentity, TreeIdentity(await DocumentAudioFixture.Data(await api.GetAsync($"/admin/document-files/{file}/structure"))));
            Assert.Equal(audioIdentity, JsonSerializer.Serialize(await AudioRows()));
            var rootNode = tree["nodes"]![0]!; var nodeId = rootNode["id"]!.GetValue<string>();
            rootNode["parseId"] = parse.ToString(); rootNode["startSequence"] = 0; rootNode["endSequence"] = 3;
            rootNode["children"]![0]!["parseId"] = parse.ToString(); rootNode["children"]![0]!["startSequence"] = 0; rootNode["children"]![0]!["endSequence"] = 1;
            rootNode["children"]![1]!["parseId"] = parse.ToString(); rootNode["children"]![1]!["startSequence"] = 2; rootNode["children"]![1]!["endSequence"] = 3;
            await DocumentAudioFixture.Data(await api.PutAsJsonAsync($"/admin/document-files/{file}/structure", new { nodes = tree["nodes"] }));
            using var http = new HttpClient(); (await http.PostAsync(search.Url + "/" + index + "/_refresh", null)).EnsureSuccessStatusCode();
            var content = await DocumentAudioFixture.Data(await api.GetAsync($"/internal/v1/structure-nodes/{nodeId}/content")); Assert.Equal(4, content["items"]!.AsArray().Count);
            var hits = await DocumentAudioFixture.Data(await api.GetAsync($"/internal/v1/search?query=audio%20binding%20beacon&phrase=true&structureNodeId={nodeId}")); Assert.Equal(4, hits["results"]!.AsArray().Count);
            Assert.Equal(audioIdentity, JsonSerializer.Serialize(await AudioRows()));
            await DocumentAudioFixture.Data(await api.PostAsync($"/admin/document-files/{file}/parse", null)); await WaitParsed(2);
            var after = await DocumentAudioFixture.Data(await api.GetAsync($"/admin/document-files/{file}/structure"));
            Assert.Equal(nodeIdentity, TreeIdentity(after)); Assert.All(DocumentAudioFixture.Nodes(after), n => { Assert.Null(n["parseId"]); Assert.Null(n["startSequence"]); Assert.Null(n["endSequence"]); });
            Assert.Equal(audioIdentity, JsonSerializer.Serialize(await AudioRows()));
            Assert.Empty((await DocumentAudioFixture.Data(await api.GetAsync($"/internal/v1/structure-nodes/{nodeId}/content")))["items"]!.AsArray());
            Assert.Empty((await DocumentAudioFixture.Data(await api.GetAsync($"/internal/v1/search?query=audio&structureNodeId={nodeId}")))["results"]!.AsArray());
            await VerifyAndPlayAll(await AudioRows());
            Assert.Equal(0, await h.Page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));

            async Task<int> ParseCount() { using var scope = h.Factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParses.CountAsync(p => p.DocumentFileId == file); }
            async Task<List<DocumentStructureAudioEntity>> AudioRows() { using var scope = h.Factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureAudio.AsNoTracking().Where(a => a.DocumentFileId == file).OrderBy(a => a.SourcePath).ToListAsync(); }
            async Task<Guid> WaitParsed(int count)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    using var scope = h.Factory.Services.CreateScope(); var rows = await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentParses.AsNoTracking().Where(p => p.DocumentFileId == file).OrderByDescending(p => p.ParsedAt).ToListAsync(timeout.Token);
                    if (rows.Count == count && rows[0].Status == DocumentParseStatus.Parsed) return rows[0].Id;
                    await Task.Delay(50, timeout.Token);
                }
            }
            async Task VerifyAndPlayAll(List<DocumentStructureAudioEntity> rows)
            {
                await h.Page.GotoAsync(h.Origin + "/health");
                await h.Page.SetContentAsync("<!doctype html><button id='play-book-track'>Play</button><audio id='book-audio' controls></audio>");
                foreach (var row in rows)
                {
                    var expected = Convert.ToHexString(SHA256.HashData(DocumentAudioFixture.Wave(Array.IndexOf(DocumentAudioFixture.Paths, row.SourcePath["generated-wave/".Length..])))).ToLowerInvariant();
                    Assert.True(await h.Page.EvaluateAsync<bool>("""
                        async ({ node, audio, expected }) => {
                          const list = await (await fetch('/internal/v1/structure-nodes/' + node + '/audio')).json();
                          const item = list.data.items.find(i => i.id === audio);
                          const url = item.playback.url;
                          const response = await fetch(url); if (response.status !== 200) return false;
                          const bytes = await response.arrayBuffer();
                          const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), n => n.toString(16).padStart(2, '0')).join('');
                          if (hash !== expected) return false;
                          const context = new AudioContext();
                          try { const decoded = await context.decodeAudioData(bytes); if (Math.abs(decoded.duration - 3) > .001 || !decoded.getChannelData(0).some(n => Math.abs(n) > .001)) return false; }
                          finally { await context.close(); }
                          window.__bookPlaying = false;
                          document.querySelector('#play-book-track').onclick = async () => {
                            const player = document.querySelector('#book-audio');
                            player.onplaying = () => { window.__bookPlaying = true; };
                            player.src = url; await player.play();
                          };
                          return new URL(url, location.href).origin === location.origin && url.startsWith('/internal/');
                        }
                        """, new { node = row.NodeId!.Value.ToString(), audio = row.Id.ToString(), expected }), "Generated recording bytes or real decode did not match.");
                    await h.Page.ClickAsync("#play-book-track");
                    await h.Page.WaitForFunctionAsync("() => { const a = document.querySelector('#book-audio'); return window.__bookPlaying && !a.paused && a.currentTime > .15 && !a.error; }");
                    await h.Page.EvaluateAsync("() => { const a = document.querySelector('#book-audio'); a.pause(); a.removeAttribute('src'); a.load(); document.querySelector('#play-book-track').onclick = null; }");
                    await h.Page.WaitForFunctionAsync("() => { const a = document.querySelector('#book-audio'); return a.readyState === 0 && a.networkState === 0; }");
                }
            }
        }
        finally { await search.DisposeAsync(); }
    }
    private static string TreeIdentity(JsonNode tree) => JsonSerializer.Serialize(DocumentAudioFixture.Nodes(tree).Select(n => new {
        id = n["id"]!.GetValue<string>(), level = n["levelId"]!.GetValue<string>(), name = n["name"]!.GetValue<string>(), order = n["sortOrder"]!.GetValue<int>() }));
    private static byte[] MinimalAudioPdf()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R >>", "<< /Length 0 >>\nstream\n\nendstream"];
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append("xref\n0 5\n0000000000 65535 f \n"); foreach (var offset in offsets) pdf.Append(offset.ToString("D10") + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
