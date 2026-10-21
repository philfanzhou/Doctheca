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
    /// reload the saved result is shown; playback uses a signed link of the public storage address.
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
        await Assertions.Expect(h.Page.GetByText("已导入 58 个文件：57 个已自动关联，1 个待处理")).ToBeVisibleAsync();

        // 2. Only the folder/name mismatch is left to review.
        var rows = h.Page.Locator("tbody tr");
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        var mismatch = h.Page.Locator("tr[data-audio-path='官方音频/unit3/9s04Unit4SectionA1e.mp3']");
        await Assertions.Expect(mismatch).ToContainTextAsync("文件夹与文件名不一致");

        // 3. Link it to Unit 4 > Section A.
        await mismatch.Locator(".el-select").ClickAsync();
        await h.Page.Locator(".el-select-dropdown__item:visible", new() { HasText = "Unit 4 Arts and Crafts > Section A" }).ClickAsync();
        await Assertions.Expect(mismatch).ToContainTextAsync("未保存");

        // 4. Clear one link and remove one file, then save.
        await h.Page.GetByRole(AriaRole.Tab, new() { Name = "全部 58" }).ClickAsync();
        var cleared = h.Page.Locator("tr[data-audio-path='官方音频/unit1/9s01Unit1SectionA1b.mp3']");
        await cleared.Locator(".el-select").HoverAsync();
        await cleared.Locator(".el-select__clear").ClickAsync();
        await Assertions.Expect(cleared).ToContainTextAsync("未保存");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "删除 官方音频/unit8/9sVocabulary08Unit8.mp3" }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "保存", Exact = true }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "确定" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("音频关联已保存")).ToBeVisibleAsync();

        // 5. A reload shows the saved result.
        await h.Page.ReloadAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(57);
        await Assertions.Expect(mismatch).ToContainTextAsync("已手动关联");
        await Assertions.Expect(mismatch.Locator(".el-select")).ToContainTextAsync("Unit 4 Arts and Crafts > Section A");
        await Assertions.Expect(cleared).ToContainTextAsync("已手动取消关联");
        await Assertions.Expect(h.Page.Locator("tr[data-audio-path='官方音频/unit8/9sVocabulary08Unit8.mp3']")).ToHaveCountAsync(0);
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Tab, new() { Name = "待处理 0" })).ToBeVisibleAsync();
        Assert.Equal(57, (await minio.KeysAsync(bucket)).Count);
        Assert.Equal(57, await CountAudioAsync(h, parseId));

        // 6. Playback uses a short-lived link of the public storage address.
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "试听 官方音频/unit2/9s01Unit2SectionA1b.mp3" }).ClickAsync();
        var source = await h.Page.Locator(".audio-player audio").GetAttributeAsync("src");
        Assert.StartsWith(minio.PublicBaseUrl + "/", source);
        Assert.Contains("X-Amz-Expires=300", source);
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
        await context.SaveChangesAsync();

        var titles = new[] { "The Changing World", "Saying Thank You", "Exploring the World", "Arts and Crafts", "Look Into Science!", "Sports and Health", "Making Our World a Better Place", "Media and You" };
        for (var u = 0; u < 8; u++)
        {
            var unitNode = Node(parse.Id, unit.Id, null, $"Unit {u + 1} {titles[u]}", u, u * 50, u * 50 + 49, now);
            context.DocumentStructureNodes.Add(unitNode);
            await context.SaveChangesAsync();
            context.DocumentStructureNodes.AddRange(
                Node(parse.Id, section.Id, unitNode.Id, "Section A", 0, u * 50, u * 50 + 19, now),
                Node(parse.Id, section.Id, unitNode.Id, "Section B", 1, u * 50 + 20, u * 50 + 39, now));
            await context.SaveChangesAsync();
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
