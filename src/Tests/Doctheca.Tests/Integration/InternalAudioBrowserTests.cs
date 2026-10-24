using Doctheca.Common.Oss;
using Doctheca.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminHostedFrontendTests
{
    [Fact]
    public async Task AnonymousInternalAudio_PlaysRealBytesInChromiumWithoutStorageUrlsOrCredentials()
    {
        var bucket = "internal-browser-" + Guid.NewGuid().ToString("N")[..12];
        var settings = minio.Settings(bucket); settings["Oss:PublicBaseUrl"] = "";
        await using var h = await Open(settings: settings, configureServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddScoped<IOssService>(sp => new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value));
        });
        var bytes = PlayableWave.Bytes(); var seed = await InternalAudioEndpointTests.SeedAsync(h.Factory.Services, bytes);
        await minio.PutAsync(bucket, $"audio/{seed.Parse:N}/{seed.Audio:N}.wav", bytes);
        try
        {
            await h.Page.GotoAsync(h.Origin + "/health");
            await h.Page.SetContentAsync("<!doctype html><button id='play'>Play</button><audio id='audio' controls></audio>");
            await h.Page.EvaluateAsync("path => { document.querySelector('#play').onclick = async () => { const r = await fetch(path); if (!r.ok) return; const j = await r.json(); const a = document.querySelector('#audio'); a.src = j.data.items[0].playback.url; await a.play(); }; }", $"/internal/v1/structure-nodes/{seed.Node:D}/audio");
            await h.Page.ClickAsync("#play");
            await h.Page.WaitForFunctionAsync("() => { const a = document.querySelector('audio'); return !a.paused && a.currentTime > 0.1; }");
            Assert.True(await h.Page.EvaluateAsync<bool>("() => { const u = new URL(document.querySelector('audio').src); return u.origin === location.origin && u.pathname.startsWith('/internal/') && !u.pathname.includes('/admin/'); }"), "Audio did not use the anonymous same-origin proxy.");
            Assert.Empty(await h.Context.CookiesAsync()); Assert.Equal(0, h.Authority.Redeems);
        }
        finally
        {
            using var scope = h.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            await db.DocumentFiles.Where(x => x.Id == seed.File).ExecuteDeleteAsync();
            await db.StructureDefinitions.Where(x => x.Id == seed.Definition).ExecuteDeleteAsync();
        }
    }
}
