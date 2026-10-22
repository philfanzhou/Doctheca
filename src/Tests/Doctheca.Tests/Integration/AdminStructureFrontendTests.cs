using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    /// <summary>
    /// Issue #89 in the real console: a structure definition is created at runtime, chosen for a
    /// document, and its levels are marked on the continuous block view by click, shift-click and
    /// drag; after saving, a reload shows exactly the same tree. Table HTML from the parse result
    /// is rendered as text only, so markup inside it never runs.
    /// </summary>
    [Fact]
    public async Task StructureIsConfiguredMarkedSavedAndReloadedInTheConsole()
    {
        await using var h = await Open();
        await h.Page.SetViewportSizeAsync(1280, 900);
        var (fileId, parseId) = await SeedStructureDocumentAsync(h);

        // 1. Definition with two levels, configured in the console.
        await h.Page.GotoAsync(h.Origin + "/#structures");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "新建结构定义" }).ClickAsync();
        await h.Page.Locator("#structure-definition-name").FillAsync("English textbook");
        await h.Page.GetByLabel("层级 1 名称").FillAsync("Unit");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "添加层级" }).ClickAsync();
        await h.Page.GetByLabel("层级 2 名称").FillAsync("Section");
        await Assertions.Expect(h.Page.GetByText("上级：Unit")).ToBeVisibleAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.Locator(".def-name", new() { HasText = "English textbook" })).ToBeVisibleAsync();

        // 2. The document chooses it.
        await h.Page.GotoAsync(h.Origin + "/#detail/" + fileId);
        // The visible select wrapper (its placeholder covers the combobox input).
        await h.Page.Locator(".kv-row-control .el-select").ClickAsync();
        await h.Page.Locator(".el-select-dropdown__item", new() { HasText = "English textbook" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("已设置结构定义")).ToBeVisibleAsync();

        // 3. Mark Unit 1 (click + shift-click), Section A inside it (drag), and Unit 2.
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "结构标注" }).ClickAsync();
        await h.Page.WaitForFunctionAsync("hash => location.hash === hash", "#structure/" + parseId);
        var block = (int sequence) => h.Page.Locator($"[data-seq=\"{sequence}\"]");
        await Assertions.Expect(block(59)).ToBeAttachedAsync();

        await block(0).ClickAsync();
        await block(29).ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Assertions.Expect(h.Page.Locator(".mark-range")).ToContainTextAsync("#0–#29");
        await Assertions.Expect(h.Page.GetByLabel("节点名称", new() { Exact = true })).ToHaveValueAsync("Unit 1 The Changing World");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "标注", Exact = true }).ClickAsync();

        await block(0).HoverAsync();
        await h.Page.Mouse.DownAsync();
        await block(7).HoverAsync();
        await block(14).HoverAsync();
        await h.Page.Mouse.UpAsync();
        await Assertions.Expect(h.Page.Locator(".mark-range")).ToContainTextAsync("#0–#14");
        await h.Page.GetByLabel("节点名称", new() { Exact = true }).FillAsync("Section A");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "标注", Exact = true }).ClickAsync();

        await block(30).ClickAsync();
        await block(59).ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "标注", Exact = true }).ClickAsync();

        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("结构已保存")).ToBeVisibleAsync();

        // 4. A reload shows exactly the same tree and ranges.
        await h.Page.ReloadAsync();
        await Assertions.Expect(block(59)).ToBeAttachedAsync();
        var names = h.Page.Locator(".tree-node .tree-name");
        await Assertions.Expect(names).ToHaveCountAsync(3);
        Assert.Equal(["Unit 1 The Changing World", "Section A", "Unit 2 Hello"],
            await names.EvaluateAllAsync<string[]>("inputs => inputs.map(i => i.value)"));
        Assert.Equal(["#0–#29", "#0–#14", "#30–#59"],
            await h.Page.Locator(".tree-node .link-btn").AllInnerTextsAsync());
        Assert.Equal(["Unit", "Section", "Unit"],
            await h.Page.Locator(".tree-node .tree-node-head .badge").AllInnerTextsAsync());
        Assert.Equal(3, await CountNodesAsync(h, parseId));

        // 5. Table markup is shown as text; nothing inside it executed.
        // Only the cell's readable text: no markup, and no script source either.
        await Assertions.Expect(h.Page.Locator(".block-table td")).ToHaveTextAsync("apple");
        Assert.Equal(0, await h.Page.Locator(".block-list img[src='x'], .block-list script").CountAsync());
        Assert.True(await h.Page.EvaluateAsync<bool>("window.__pwned === undefined"));
    }

    private static async Task<(string FileId, string ParseId)> SeedStructureDocumentAsync(BrowserHarness h)
    {
        using var scope = h.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var file = new DocumentFileEntity { Id = Guid.NewGuid(), FileName = "pep-grade9.pdf", ContentType = "application/pdf" };
        var parse = new DocumentParseEntity { Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = "parsed", ParsedAt = DateTimeOffset.UtcNow };
        context.DocumentFiles.Add(file);
        context.DocumentParses.Add(parse);
        for (var i = 0; i < 60; i++)
        {
            var (type, subType, format, content) = i switch
            {
                0 => ("title", "heading-1", "plain", "Unit 1 The Changing World"),
                30 => ("title", "heading-1", "plain", "Unit 2 Hello"),
                5 => ("table", null, "html",
                    "<table><tr><th>Word</th></tr><tr><td><img src=x onerror=\"window.__pwned=1\">apple<script>window.__pwned=2</script></td></tr></table>"),
                _ => ("text", null, "plain", $"Paragraph {i}"),
            };
            context.DocumentParseBlocks.Add(new DocumentParseBlockEntity
            {
                ParseId = parse.Id,
                PageId = i / 10,
                SortIndex = i % 10,
                Sequence = i,
                BlockType = type,
                SubType = subType,
                TextFormat = format,
                TextContent = content,
                BlockData = "{}",
            });
        }
        await context.SaveChangesAsync();
        return (file.Id.ToString(), parse.Id.ToString());
    }

    private static async Task<int> CountNodesAsync(BrowserHarness h, string parseId)
    {
        using var scope = h.Factory.Services.CreateScope();
        var id = Guid.Parse(parseId);
        return await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureNodes.CountAsync(n => n.ParseId == id);
    }
}
