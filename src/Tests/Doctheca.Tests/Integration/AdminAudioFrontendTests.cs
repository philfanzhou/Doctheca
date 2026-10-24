using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Tests.Structure;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    /// <summary>
    /// Issue #90 in the real console against real object storage: a folder with the 58 official
    /// audio paths (synthetic bytes) is imported, the folder/name mismatch is the only file left to
    /// review, the administrator links it, clears another link and removes a file, and after a
    /// reload the saved result is shown.
    /// </summary>
    [Fact]
    public async Task OfficialAudioIsImportedReviewedAndSavedInTheConsole()
    {
        var bucket = "console-audio-" + Guid.NewGuid().ToString("N")[..12];
        await using var h = await Open(settings: minio.Settings(bucket), configureServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
        });
        await h.Page.SetViewportSizeAsync(1280, 900);
        var parseId = await SeedTextbookStructureAsync(h);

        var folder = Path.Combine(h.Root, "官方音频");
        foreach (var path in StructureAudioMatcherTests.OfficialAudioPaths)
        {
            var target = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = new byte[200];
            "ID3"u8.CopyTo(bytes);
            bytes[3] = 3;
            File.WriteAllBytes(target, bytes);
        }
        File.WriteAllText(Path.Combine(folder, ".DS_Store"), "metadata");

        // 1. Import the whole folder.
        await h.Page.GotoAsync(h.Origin + "/#audio/" + parseId);
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "选择文件夹" })).ToBeEnabledAsync();
        await h.Page.Locator("[data-testid=audio-folder-input]").SetInputFilesAsync(folder);
        await Assertions.Expect(h.Page.GetByText("已选择 58 个文件，确认目标后点击保存")).ToBeVisibleAsync();

        // 2. Only the folder/name mismatch is left to review.
        var rows = h.Page.Locator("tbody tr");
        await h.Page.GetByRole(AriaRole.Tab, new() { Name = "待处理 1" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        var mismatch = h.Page.Locator("tr[data-audio-path='官方音频/unit3/9s04Unit4SectionA1e.mp3']");
        await Assertions.Expect(mismatch).ToContainTextAsync("文件夹与文件名不一致");

        // 3. Link it to Unit 4 > Section A.
        await mismatch.Locator(".el-select").ClickAsync();
        await h.Page.Locator(".el-select-dropdown__item:visible", new() { HasText = "Unit 4 Arts and Crafts > Section A" }).ClickAsync();
        await Assertions.Expect(mismatch).ToContainTextAsync("未保存");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("音频关联已保存", new() { Exact = true })).ToBeVisibleAsync();

        // 4. Clear one link and remove one file, then save.
        await h.Page.GetByRole(AriaRole.Tab, new() { Name = "全部 58" }).ClickAsync();
        var cleared = h.Page.Locator("tr[data-audio-path='官方音频/unit1/9s01Unit1SectionA1b.mp3']");
        await cleared.Locator(".el-select").HoverAsync();
        await cleared.Locator(".el-select__clear").ClickAsync();
        await Assertions.Expect(cleared).ToContainTextAsync("未保存");
        await h.Page.Locator("[data-testid=audio-file-input]").SetInputFilesAsync(new FilePayload
        {
            Name = "Unit2SectionA.mp3", MimeType = "audio/mpeg", Buffer = "ID3xxxxxxxxx"u8.ToArray(),
        });
        await Assertions.Expect(h.Page.GetByText("已选择 1 个文件，确认目标后点击保存")).ToBeVisibleAsync();
        await Assertions.Expect(cleared).ToContainTextAsync("未保存");
        foreach (var action in new Func<Task>[]
        {
            () => h.Page.GetByRole(AriaRole.Button, new() { Name = "重新加载" }).ClickAsync(),
            () => h.Page.GetByRole(AriaRole.Button, new() { Name = "返回结构标注" }).ClickAsync(),
            () => h.Page.EvaluateAsync("location.hash = '#docs'"),
            () => h.Page.Locator(".sidebar").GetByText("文档管理", new() { Exact = true }).ClickAsync(),
            () => h.Page.GetByRole(AriaRole.Button, new() { Name = "退出", Exact = true }).ClickAsync(),
        })
        {
            await action();
            await Assertions.Expect(h.Page.Locator(".el-message-box:visible").GetByRole(AriaRole.Button, new() { Name = "取消", Exact = true })).ToBeVisibleAsync();
            await h.Page.Locator(".el-message-box:visible").GetByRole(AriaRole.Button, new() { Name = "取消", Exact = true }).ClickAsync();
            await Assertions.Expect(h.Page.Locator(".el-message-box:visible")).ToHaveCountAsync(0);
            await Assertions.Expect(cleared).ToContainTextAsync("未保存");
            Assert.Contains("#audio/", h.Page.Url);
        }

        var removed = h.Page.Locator("tr[data-audio-path='官方音频/unit8/9sVocabulary08Unit8.mp3']");
        var unchanged = h.Page.Locator("tr[data-audio-path='官方音频/unit1/9sVocabulary01Unit1.mp3']");
        await removed.Locator("button[aria-label^='删除 '], button[aria-label^='撤销删除 ']").ClickAsync();

        // Hold the real PUT response to exercise the entire save, including confirmation.
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Page.RouteAsync("**/admin/document-parses/" + parseId + "/audio", async route =>
        {
            if (route.Request.Method != "POST" || route.Request.Url.Contains("preview=true", StringComparison.Ordinal)) { await route.ContinueAsync(); return; }
            var response = await route.FetchAsync();
            received.TrySetResult(route.Request.PostData ?? "");
            await release.Task;
            await route.FulfillAsync(new() { Response = response });
        });
        try
        {
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
            await Assertions.Expect(h.Page.Locator(".el-message-box:visible")).ToBeVisibleAsync();
            await AssertAudioDraftLockedAsync(h.Page);
            Assert.False(received.Task.IsCompleted, "Save was sent before deletion confirmation.");
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "取消", Exact = true }).ClickAsync();
            await Assertions.Expect(h.Page.Locator(".el-message-box:visible")).ToHaveCountAsync(0);
            await Assertions.Expect(unchanged.GetByRole(AriaRole.Combobox)).ToBeEnabledAsync();
            await Assertions.Expect(removed.Locator("button[aria-label^='删除 '], button[aria-label^='撤销删除 ']")).ToBeEnabledAsync();
            await Assertions.Expect(cleared).ToContainTextAsync("未保存");
            await Assertions.Expect(removed).ToContainTextAsync("待删除");

            // Undo and mark again after cancellation to prove the draft is editable again.
            await removed.Locator("button[aria-label^='删除 '], button[aria-label^='撤销删除 ']").ClickAsync();
            await Assertions.Expect(removed).Not.ToContainTextAsync("待删除");
            await removed.Locator("button[aria-label^='删除 '], button[aria-label^='撤销删除 ']").ClickAsync();
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
            await h.Page.GetByRole(AriaRole.Button, new() { Name = "确定", Exact = true }).ClickAsync();
            var sent = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("name=links", sent.Replace("\"", ""));
            Assert.Contains("name=removeIds", sent.Replace("\"", ""));
            await AssertAudioDraftLockedAsync(h.Page);

            // Native disabled controls reject both removal and undo while the response waits.
            await unchanged.GetByRole(AriaRole.Button, new() { Name = "删除", Exact = false }).EvaluateAsync("button => button.click()");
            await removed.Locator("button[aria-label^='删除 '], button[aria-label^='撤销删除 ']").EvaluateAsync("button => button.click()");
            await Assertions.Expect(unchanged).Not.ToContainTextAsync("待删除");
            await Assertions.Expect(removed).ToContainTextAsync("待删除");
            await Assertions.Expect(cleared).ToContainTextAsync("未保存");
            release.TrySetResult();
            await Assertions.Expect(h.Page.GetByText("音频关联已保存")).ToBeVisibleAsync();
            await Assertions.Expect(unchanged.GetByRole(AriaRole.Combobox)).ToBeEnabledAsync();
            await Assertions.Expect(unchanged.GetByRole(AriaRole.Button, new() { Name = "删除", Exact = false })).ToBeEnabledAsync();
            await Assertions.Expect(h.Page.Locator(".page-actions .badge.amber")).ToHaveCountAsync(0);
        }
        finally
        {
            release.TrySetResult();
        }

        // 5. A reload shows the saved result.
        await h.Page.ReloadAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(58);
        await Assertions.Expect(mismatch).ToContainTextAsync("已手动关联");
        await Assertions.Expect(mismatch.Locator(".el-select")).ToContainTextAsync("Unit 4 Arts and Crafts > Section A");
        await Assertions.Expect(cleared).ToContainTextAsync("已手动取消关联");
        await Assertions.Expect(h.Page.Locator("tr[data-audio-path='官方音频/unit8/9sVocabulary08Unit8.mp3']")).ToHaveCountAsync(0);
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Tab, new() { Name = "待处理 0" })).ToBeVisibleAsync();
        Assert.Equal(58, (await minio.KeysAsync(bucket)).Count);
        Assert.Equal(58, await CountAudioAsync(h, parseId));


    }

    private static async Task AssertAudioDraftLockedAsync(IPage page)
    {
        // Disabled select inputs block changing and clearing every node, even an open selection.
        await Assertions.Expect(page.Locator(".audio-node input:not(:disabled)")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".audio-node .el-select__clear:visible")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".audio-actions button:not(:disabled)")).ToHaveCountAsync(0);
    }

    private static async Task<string> SeedTextbookStructureAsync(BrowserHarness h)
    {
        using var scope = h.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var now = DateTimeOffset.UtcNow;
        var definition = new StructureDefinitionEntity { Id = Guid.NewGuid(), Name = "Audio textbook " + Guid.NewGuid().ToString("N")[..6], CreatedAt = now, UpdatedAt = now };
        var unit = new StructureLevelEntity { Id = Guid.NewGuid(), DefinitionId = definition.Id, Name = "Unit", SortOrder = 0 };
        var section = new StructureLevelEntity { Id = Guid.NewGuid(), DefinitionId = definition.Id, Name = "Section", ParentLevelId = unit.Id, SortOrder = 1 };
        var file = new DocumentFileEntity { Id = Guid.NewGuid(), FileName = "pep-grade9.pdf", ContentType = "application/pdf", StructureDefinitionId = definition.Id };
        var parse = new DocumentParseEntity { Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = "parsed", ParsedAt = now };
        context.StructureDefinitions.Add(definition);
        context.StructureLevels.AddRange(unit, section);
        context.DocumentFiles.Add(file);
        context.DocumentParses.Add(parse);
        for (var i = 0; i < 400; i++)
        {
            context.DocumentParseBlocks.Add(new DocumentParseBlockEntity
            {
                ParseId = parse.Id, PageId = i / 10, SortIndex = i % 10, Sequence = i,
                BlockType = "text", TextFormat = "plain", TextContent = $"Paragraph {i}", BlockData = "{}",
            });
        }
        await context.SaveOwnedChangesAsync();

        var titles = new[] { "The Changing World", "Saying Thank You", "Exploring the World", "Arts and Crafts", "Look Into Science!", "Sports and Health", "Making Our World a Better Place", "Media and You" };
        for (var u = 0; u < 8; u++)
        {
            var unitNode = Node(parse.Id, unit.Id, null, $"Unit {u + 1} {titles[u]}", u, u * 50, u * 50 + 49, now);
            context.DocumentStructureNodes.Add(unitNode);
            await context.SaveOwnedChangesAsync();
            context.DocumentStructureNodes.AddRange(
                Node(parse.Id, section.Id, unitNode.Id, "Section A", 0, u * 50, u * 50 + 19, now),
                Node(parse.Id, section.Id, unitNode.Id, "Section B", 1, u * 50 + 20, u * 50 + 39, now));
            await context.SaveOwnedChangesAsync();
        }
        return parse.Id.ToString();
    }

    private static DocumentStructureNodeEntity Node(Guid parseId, Guid levelId, Guid? parentId, string name, int order, int start, int end, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(), ParseId = parseId, LevelId = levelId, ParentId = parentId, Name = name, SortOrder = order,
            StartSequence = start, EndSequence = end, CreatedAt = now, UpdatedAt = now,
        };

    private static async Task<int> CountAudioAsync(BrowserHarness h, string parseId)
    {
        using var scope = h.Factory.Services.CreateScope();
        var id = Guid.Parse(parseId);
        return await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentStructureAudio.CountAsync(a => a.ParseId == id);
    }
}
