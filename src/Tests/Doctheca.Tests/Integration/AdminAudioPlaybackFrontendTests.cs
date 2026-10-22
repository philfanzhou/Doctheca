using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Doctheca.Common.Oss;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    [Fact]
    public async Task NodeAudioPlaysRealWaveHandlesFailuresAndReleasesResources()
    {
        var bucket = "playback-browser-" + Guid.NewGuid().ToString("N")[..12];
        await using var h = await Open(settings: minio.Settings(bucket), configureServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
        });
        var parseId = await SeedTextbookStructureAsync(h);
        await h.Page.GotoAsync(h.Origin + "/#audio/" + parseId);
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "选择文件夹" })).ToBeEnabledAsync();
        await h.Page.Locator("[data-testid=audio-file-input]").SetInputFilesAsync(new[] {
            new FilePayload { Name = "Unit1SectionA.wav", MimeType = "audio/wav", Buffer = PlayableWave.Bytes() },
            new FilePayload { Name = "Unit2SectionA.wav", MimeType = "audio/wav", Buffer = PlayableWave.Bytes() },
            new FilePayload { Name = "Unit3SectionA.wav", MimeType = "audio/wav", Buffer = "RIFFxxxxWAVEbroken-media"u8.ToArray() },
        });
        await Assertions.Expect(h.Page.GetByText("已导入 3 个文件：3 个已自动关联，0 个待处理")).ToBeVisibleAsync();
        var first = h.Page.GetByRole(AriaRole.Button, new() { Name = "播放 Unit1SectionA.wav", Exact = true });
        var second = h.Page.GetByRole(AriaRole.Button, new() { Name = "播放 Unit2SectionA.wav", Exact = true });
        var player = h.Page.Locator("audio");
        var links = 0;
        h.Page.Request += (_, request) => { if (request.Url.EndsWith("/playback-link", StringComparison.Ordinal)) links++; };
        await h.Page.EvaluateAsync("() => { window.__audioPlaying = false; document.addEventListener('playing', () => window.__audioPlaying = true, true) }");
        await first.ClickAsync();
        await h.Page.WaitForFunctionAsync("() => { const p = document.querySelector('audio'); return p && !p.paused && p.currentTime > 0.1 }");
        Assert.True(await h.Page.EvaluateAsync<bool>("window.__audioPlaying === true"), "No real media playing event occurred.");
        await h.Page.EvaluateAsync("window.__releasedAudio = document.querySelector('audio')");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "关闭播放", Exact = true }).ClickAsync();
        await AssertReleasedAudio(h.Page);
        await Assertions.Expect(player).ToHaveCountAsync(0);
        await first.ClickAsync();
        await h.Page.WaitForFunctionAsync("() => document.querySelector('audio')?.currentTime > 0.1");
        Assert.Equal(2, links);
        await h.Page.EvaluateAsync("window.__releasedAudio = document.querySelector('audio')");
        await h.Page.EvaluateAsync("() => { window.__audioEmptied = 0; document.querySelector('audio').addEventListener('emptied', () => window.__audioEmptied++) }");
        await second.ClickAsync();
        // Switching releases the prior source before attaching the next credential.
        await h.Page.WaitForFunctionAsync("() => document.querySelector('audio')?.currentTime > 0.1");
        Assert.Equal(3, links);
        Assert.True(await h.Page.EvaluateAsync<bool>("window.__audioEmptied > 0"), "Switching did not release the previous media resource.");
        await h.Page.EvaluateAsync("window.__releasedAudio = document.querySelector('audio')");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "返回结构标注", Exact = true }).ClickAsync();
        await Assertions.Expect(player).ToHaveCountAsync(0);
        await AssertReleasedAudio(h.Page);

        await h.Page.GotoAsync(h.Origin + "/#audio/" + parseId);
        await h.Page.ReloadAsync();
        await Assertions.Expect(first).ToBeEnabledAsync();
        await first.ClickAsync();
        await h.Page.WaitForFunctionAsync("() => document.querySelector('audio')?.currentTime > 0.1");
        Assert.Equal(4, links);
        Assert.Equal(0, await h.Page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "关闭播放", Exact = true }).ClickAsync();

        var pattern = "**/admin/document-parses/" + parseId + "/audio/*/playback-link";
        foreach (var (status, message) in new[] { (404, "音频已不存在，请重新加载列表。"), (503, "音频播放暂时不可用，请稍后重试。") })
        {
            await h.Page.RouteAsync(pattern, route => route.FulfillAsync(new() { Status = status, ContentType = "application/json", Body = "{\"success\":false}" }));
            await first.ClickAsync();
            await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync(message);
            await Assertions.Expect(player).ToHaveCountAsync(0);
            await h.Page.UnrouteAsync(pattern);
        }
        await h.Page.RouteAsync(pattern, route => route.AbortAsync());
        await first.ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("无法播放音频，请检查网络或文件格式后重试。");
        await Assertions.Expect(player).ToHaveCountAsync(0);
        await h.Page.UnrouteAsync(pattern);

        await h.Page.RouteAsync(pattern, async route =>
        {
            var response = await route.FetchAsync();
            var body = JsonNode.Parse(await response.TextAsync())!;
            body["data"]!["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1);
            await route.FulfillAsync(new() { Response = response, Body = body.ToJsonString() });
        });
        await first.ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("播放链接已过期，请重新点击播放。");
        await h.Page.UnrouteAsync(pattern);
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "播放 Unit3SectionA.wav", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("格式");
        await Assertions.Expect(player).ToHaveCountAsync(0);
        await first.ClickAsync();
        await h.Page.WaitForFunctionAsync("() => document.querySelector('audio')?.currentTime > 0.1");
        Assert.Equal(0, await h.Page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
    }

    private static async Task AssertReleasedAudio(IPage page)
    {
        await page.WaitForTimeoutAsync(100);
        var state = await page.EvaluateAsync<string>("() => { const p = window.__releasedAudio; return JSON.stringify({ exists: !!p, paused: p?.paused, hasSrc: p?.hasAttribute('src'), emptyCurrentSrc: p?.currentSrc === '', ready: p?.readyState, network: p?.networkState }) }");
        Assert.True(await page.EvaluateAsync<bool>("() => { const p = window.__releasedAudio; return p && p.paused && !p.hasAttribute('src') && p.readyState === 0 && p.networkState === 0 }"), "Media resource was not released: " + state);
    }
}
