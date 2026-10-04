using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    private static async Task NavigateSidebar(BrowserHarness h, string label, int width)
    {
        if (width < 900) await h.Page.Locator(".hamburger").ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync();
        await h.Page.WaitForFunctionAsync("hash => location.hash === hash", label == "文档管理" ? "#docs" : "#search");
    }

    [Theory]
    [InlineData(1280)][InlineData(390)]
    public async Task SearchDrawerReleasesPageClicksTabAndFocusAndPreservesFilters(int width)
    {
        await using var h = await Open();
        await h.Page.SetViewportSizeAsync(width, 720);
        await h.Page.GotoAsync(h.Origin + "/#search");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        var opener = h.Page.GetByRole(AriaRole.Button, new() { Name = "高级筛选", Exact = true });
        await Assertions.Expect(opener).ToBeVisibleAsync();
        // Actual navigation clicks cover the never-opened invisible-overlay failure.
        await NavigateSidebar(h, "文档管理", width);
        Assert.EndsWith("#docs", h.Page.Url);
        await NavigateSidebar(h, "检索测试", width);
        var query = h.Page.GetByPlaceholder("输入单词或短语进行检索…");
        await query.ClickAsync();
        await query.FillAsync("probe");
        for (var i = 0; i < 25; i++)
        {
            await h.Page.Keyboard.PressAsync("Tab");
            Assert.False(await h.Page.EvaluateAsync<bool>("!!document.activeElement?.closest('.drawer')"));
        }
        foreach (var method in new[] { "escape", "button", "overlay", "apply" })
        {
            await opener.ClickAsync();
            var field = h.Page.GetByPlaceholder("如 table_caption / code / algorithm / image_body");
            await field.FillAsync("kept-value");
            Assert.Equal("hidden", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
            if (method == "escape") await h.Page.Keyboard.PressAsync("Escape");
            if (method == "button") await h.Page.GetByRole(AriaRole.Button, new() { Name = "关闭", Exact = true }).ClickAsync();
            if (method == "overlay") await h.Page.Locator(".drawer-overlay").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
            if (method == "apply") await h.Page.GetByRole(AriaRole.Button, new() { Name = "应用筛选", Exact = true }).ClickAsync();
            await Assertions.Expect(h.Page.Locator(".drawer")).ToHaveAttributeAsync("aria-hidden", "true");
            if (method != "overlay") await Assertions.Expect(opener).ToBeFocusedAsync();
            await query.ClickAsync();
            await opener.ClickAsync();
            await Assertions.Expect(field).ToHaveValueAsync("kept-value");
            await h.Page.Keyboard.PressAsync("Escape");
        }
        await NavigateSidebar(h, "文档管理", width);
        Assert.EndsWith("#docs", h.Page.Url);
        await NavigateSidebar(h, "检索测试", width);
        await h.Page.EvaluateAsync("document.body.style.overflow = 'scroll'");
        await opener.ClickAsync();
        // Real route teardown while open restores the scroll state captured on opening.
        await h.Page.EvaluateAsync("location.hash = '#docs'");
        await Assertions.Expect(h.Page.Locator(".drawer")).ToHaveCountAsync(0);
        Assert.Equal("scroll", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
    }

    private static async Task<string> BuildDrawerHarness()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "frontend", "package.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Frontend source unavailable.");
        var output = Path.Combine(Path.GetTempPath(), "doctheca-drawer-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "frontend/tests/drawer-harness.mjs"));
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        Assert.True(process.ExitCode == 0, "Current drawer harness compilation failed.");
        return output;
    }

    [Theory]
    [InlineData(1280)][InlineData(390)]
    public async Task SharedDrawerInitialOpenRapidToggleAndUnmountRestoreOwnedState(int width)
    {
        await using var h = await Open();
        await h.Page.SetViewportSizeAsync(width, 720);
        var output = await BuildDrawerHarness();
        try
        {
            await h.Page.RouteAsync("**/drawer-probe/**", async route =>
            {
                var name = Path.GetFileName(new Uri(route.Request.Url).AbsolutePath);
                Assert.Contains(name, new[] { "index.html", "drawer.js", "icons.js", "vue.js" });
                await route.FulfillAsync(new() { ContentType = name.EndsWith(".js") ? "text/javascript" : "text/html", Body = await File.ReadAllTextAsync(Path.Combine(output, name)) });
            });
            foreach (var initial in new[] { false, true })
            {
                await h.Page.GotoAsync(h.Origin + "/drawer-probe/index.html?initial=" + initial.ToString().ToLowerInvariant());
                await h.Page.WaitForFunctionAsync("window.ready === true");
                Assert.Equal(initial ? "hidden" : "scroll", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
                if (initial) {
                    await h.Page.Locator("#inside").FillAsync("initial-open");
                    await h.Page.Keyboard.PressAsync("Escape");
                } else {
                    await h.Page.Keyboard.PressAsync("Escape");
                    Assert.Equal(0, await h.Page.EvaluateAsync<int>("control.closes"));
                }
                var pageButton = h.Page.Locator("#underlying");
                await pageButton.ClickAsync();
                await Assertions.Expect(pageButton).ToHaveTextAsync("Page 1");
                await h.Page.Locator("#opener").FocusAsync();
                for (var i = 0; i < 8; i++) {
                    await h.Page.Keyboard.PressAsync("Tab");
                    Assert.False(await h.Page.EvaluateAsync<bool>("!!document.activeElement?.closest('.drawer')"));
                }
                await h.Page.Locator("#opener").ClickAsync();
                await h.Page.Locator("#inside").FillAsync("keep");
                await h.Page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(h.Page.Locator("#opener")).ToBeFocusedAsync();
                Assert.Equal("scroll", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
                await h.Page.EvaluateAsync("control.rapid()");
                await h.Page.Locator("#inside").ClickAsync();
                await Assertions.Expect(h.Page.Locator("#inside")).ToHaveValueAsync("keep");
                Assert.Equal("hidden", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
                await pageButton.FocusAsync();
                await h.Page.EvaluateAsync("control.set(false)");
                await Assertions.Expect(pageButton).ToBeFocusedAsync();
                await pageButton.ClickAsync();
                await h.Page.Locator("#opener").ClickAsync();
                await h.Page.Locator("#inside").FocusAsync();
                await h.Page.EvaluateAsync("control.unmount()");
                await Assertions.Expect(h.Page.Locator(".drawer,.drawer-overlay")).ToHaveCountAsync(0);
                await Assertions.Expect(h.Page.Locator("#opener")).ToBeFocusedAsync();
                Assert.Equal("scroll", await h.Page.EvaluateAsync<string>("document.body.style.overflow"));
                var closes = await h.Page.EvaluateAsync<int>("control.closes");
                await h.Page.Keyboard.PressAsync("Escape");
                Assert.Equal(closes, await h.Page.EvaluateAsync<int>("control.closes"));
                await pageButton.ClickAsync();
            }
        }
        finally { Directory.Delete(output, true); }
    }
}
