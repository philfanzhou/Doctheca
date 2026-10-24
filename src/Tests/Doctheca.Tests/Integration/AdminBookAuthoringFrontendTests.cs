using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Service.StructaDoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    [Fact]
    public async Task BrowserCreatesBookWithExternalServicesUnconfigured()
    {
        await using var h = await Open();
        await h.Page.GotoAsync(h.Origin + "/#docs");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "新建书籍", Exact = true }).ClickAsync();
        await h.Page.GetByLabel("书名", new() { Exact = true }).FillAsync("Unconfigured book");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存书籍", Exact = true }).ClickAsync();
        await h.Page.WaitForFunctionAsync("location.hash.startsWith('#detail/')");
        await h.Page.ReloadAsync();
        await Assertions.Expect(h.Page.GetByText("无源文件时不创建解析", new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task SourceFreeBook_FourteenUnitsReloadAttachParseAndBindThroughTheRealConsole()
    {
        var upstream = new Mock<IStructaDocClient>(); var source = Guid.NewGuid();
        upstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocDocumentResponse { Id = source });
        upstream.Setup(u => u.CreateParseRunAsync(source, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new() { Id = Guid.NewGuid(), DocumentId = source, Status = "succeeded" });
        upstream.Setup(u => u.GetParseRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => new() { Id = id, DocumentId = source, Status = "succeeded" });
        upstream.Setup(u => u.GetAllBlocksAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Range(0, 40).Select(i => new StructaDocBlockResponse { Id = Guid.NewGuid(), Sequence = i, PageNumber = 1, Type = "text", Content = "Paragraph " + i }).ToList());
        upstream.Setup(u => u.GetPagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        upstream.Setup(u => u.GetAssetsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        upstream.Setup(u => u.GetMarkdownAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("Paragraph");
        await using var h = await Open(settings: new Dictionary<string, string?> { ["StructaDoc:BaseUrl"] = "http://structadoc.test", ["StructaDoc:ApiKey"] = "test-only" }, configureServices: services => { services.RemoveAll<IStructaDocClient>(); services.AddSingleton(upstream.Object); });
        Guid definitionId, unitId, sectionId;
        using (var scope = h.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var definition = new StructureDefinitionEntity { Name = "Book authoring " + Guid.NewGuid() }; db.StructureDefinitions.Add(definition); definitionId = definition.Id;
            var unit = new StructureLevelEntity { DefinitionId = definition.Id, Name = "Unit" }; db.StructureLevels.Add(unit); unitId = unit.Id;
            var section = new StructureLevelEntity { DefinitionId = definition.Id, Name = "Section", ParentLevelId = unit.Id, SortOrder = 1 }; db.StructureLevels.Add(section); sectionId = section.Id; await db.SaveChangesAsync();
        }
        await h.Page.SetViewportSizeAsync(1600, 1000);
        await h.Page.GotoAsync(h.Origin + "/#docs");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "新建书籍", Exact = true }).ClickAsync();
        await h.Page.GetByLabel("书名", new() { Exact = true }).FillAsync("PEP English Grade 9");
        await h.Page.GetByLabel("学科", new() { Exact = true }).FillAsync("English");
        await h.Page.GetByLabel("年级", new() { Exact = true }).FillAsync("9");
        await h.Page.GetByLabel("年份", new() { Exact = true }).FillAsync("2026");
        await h.Page.GetByLabel("书籍结构定义").SelectOptionAsync(definitionId.ToString());
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存书籍" }).ClickAsync();
        await h.Page.WaitForFunctionAsync("location.hash.startsWith('#detail/')");
        var fileId = (await h.Page.EvaluateAsync<string>("location.hash")).Split('/')[1];
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(h.Page.GetByText("无源文件时不创建解析", new() { Exact = false })).ToBeVisibleAsync();
        upstream.Verify(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "编辑书籍章节" }).ClickAsync();
        await h.Page.WaitForFunctionAsync("location.hash.startsWith('#document-structure/')");
        for (var i = 1; i <= 14; i++)
        {
            await h.Page.GetByLabel("新章节名称", new() { Exact = true }).FillAsync("Unit " + i);
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "添加章节", Exact = true }).ClickAsync();
        }
        await Assertions.Expect(h.Page.Locator(".chapter-row")).ToHaveCountAsync(14);
        var unit1 = h.Page.Locator(".chapter-row").First;
        var unit1Id = await unit1.GetAttributeAsync("data-node-id");
        await h.Page.GetByLabel("上级章节", new() { Exact = true }).SelectOptionAsync(unit1Id!);
        await h.Page.GetByLabel("新章节名称", new() { Exact = true }).FillAsync("Section <img src=x onerror=window.__bookPwned=1>");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "添加章节", Exact = true }).ClickAsync();
        await h.Page.GetByLabel("新章节名称", new() { Exact = true }).FillAsync("Temporary section");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "添加章节", Exact = true }).ClickAsync();
        // Locate by the controlled row index; input values are DOM properties, not HTML attributes.
        await h.Page.Locator(".chapter-row").Nth(2).GetByRole(AriaRole.Button, new() { Name = "删除章节", Exact = true }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "删除", Exact = true }).ClickAsync();
        await unit1.Locator(".chapter-name").FillAsync("Unit 1 renamed");
        await unit1.GetByRole(AriaRole.Button, new() { Name = "下移", Exact = true }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存章节", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("章节已保存", new() { Exact = true })).ToBeVisibleAsync();
        var before = await h.Page.Locator(".chapter-row").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.nodeId)");
        await h.Page.ReloadAsync();
        await Assertions.Expect(h.Page.Locator(".chapter-row")).ToHaveCountAsync(15);
        Assert.Equal(before, await h.Page.Locator(".chapter-row").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.nodeId)"));
        Assert.Equal("Unit 2", await h.Page.Locator(".chapter-name").First.InputValueAsync());
        Assert.True(await h.Page.EvaluateAsync<bool>("window.__bookPwned === undefined"));
        Assert.Equal(0, await h.Page.Locator(".chapter-list img, .chapter-list script").CountAsync());
        await h.Page.Locator(".chapter-name").First.FillAsync("Unsaved");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "返回书籍详情", Exact = true }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "继续编辑", Exact = true }).ClickAsync();
        Assert.Contains("#document-structure/", h.Page.Url);
        await h.Page.Locator(".chapter-name").First.FillAsync("Unit 2");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "返回书籍详情", Exact = true }).ClickAsync();
        await h.Page.GetByLabel("附加首个源文件", new() { Exact = true }).SetInputFilesAsync(new FilePayload { Name = "different-file-name.pdf", MimeType = "application/pdf", Buffer = "%PDF-1.7 test"u8.ToArray() });
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true })).ToBeVisibleAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "VLM 解析", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "结构标注", Exact = true })).ToBeVisibleAsync(new() { Timeout = 25000 });
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "编辑书籍章节", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.Locator(".binding-state").First).ToHaveTextAsync("未绑定正文");
        Assert.Equal(before, await h.Page.Locator(".chapter-row").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.nodeId)"));
        await h.Page.Locator(".chapter-row").First.GetByRole(AriaRole.Button, new() { Name = "选择", Exact = true }).ClickAsync();
        var parseOption = await h.Page.GetByLabel("正文解析", new() { Exact = true }).Locator("option").Nth(1).GetAttributeAsync("value");
        await h.Page.GetByLabel("正文解析", new() { Exact = true }).SelectOptionAsync(parseOption!);
        await Assertions.Expect(h.Page.Locator("[data-seq='39']")).ToBeVisibleAsync();
        await h.Page.GetByLabel("起始块", new() { Exact = true }).SelectOptionAsync("20");
        await h.Page.GetByLabel("结束块", new() { Exact = true }).SelectOptionAsync("29");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "绑定到所选章节", Exact = true }).ClickAsync();
        var bindingResponse = await h.Page.RunAndWaitForResponseAsync(
            () => h.Page.GetByRole(AriaRole.Button, new() { Name = "保存章节", Exact = true }).ClickAsync(),
            r => r.Request.Method == "PUT" && r.Url.EndsWith("/structure"));
        Assert.Equal(200, bindingResponse.Status);
        await h.Page.ReloadAsync(); await Assertions.Expect(h.Page.Locator(".binding-state").First).ToHaveTextAsync("#20–#29");
        Assert.Equal(before, await h.Page.Locator(".chapter-row").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.nodeId)"));
        var csrf = await h.Page.EvaluateAsync<int[]>("""
            async id => {
              const pair = await (await fetch('/admin/auth/oidc/csrf')).json();
              const json = { fileName: 'CSRF must not create' };
              const route = '/admin/document-files/' + id + '/structure';
              const send = (url, body, token) => fetch(url, { method: url === '/admin/document-files' ? 'POST' : 'PUT', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json', ...(token ? { 'X-CSRF-TOKEN': token } : {}) } });
              const missing = await send('/admin/document-files', json);
              const bad = await send(route, { nodes: [] }, 'invalid');
              const source = await fetch('/admin/document-files/' + id + '/source', { method: 'POST', body: new FormData() });
              const current = (await (await fetch(route)).json()).data;
              const valid = await send(route, { nodes: current.nodes }, pair.token);
              return [missing.status, bad.status, source.status, valid.status];
            }
            """, fileId);
        Assert.Equal([403, 403, 403, 200], csrf);
        using var finalScope = h.Factory.Services.CreateScope(); var final = finalScope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var savedBook = await final.DocumentFiles.AsNoTracking().SingleAsync(f => f.Id == Guid.Parse(fileId));
        Assert.Equal("PEP English Grade 9", savedBook.FileName); Assert.Equal("English", savedBook.Subject); Assert.Equal("9", savedBook.Grade);
    }
}
