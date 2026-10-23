using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Service.InternalApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class InternalCatalogEndpointTests
{
    private static DocumentStructureAudioEntity Audio(Guid parse, Guid? node, string status = "matched") => new()
    {
        ParseId = parse, NodeId = node, SourcePath = Guid.NewGuid() + ".mp3", ContentType = "audio/mpeg",
        SizeBytes = 10, ObjectKey = "object-key-canary-" + Guid.NewGuid(), MatchStatus = status
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioMetadata_IsRelationalDirectCurrentMembershipWithoutStorageOrTokens(bool malformedStorage)
    {
        var tokens = 0;
        var sql = new CatalogSql();
        using var factory = CreateFactory(settings: new Dictionary<string, string?>
        {
            ["InternalApi:Enabled"] = "true", ["DocumentAudio:Enabled"] = malformedStorage ? "true" : "false",
            ["DocumentAudio:MaxFileBytes"] = malformedStorage ? "invalid-size" : "1048576",
            ["Oss:InternalEndpoint"] = "", ["Oss:PublicBaseUrl"] = "invalid-public-address"
        }, configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddScoped<IOssService>(_ => throw new InvalidOperationException("Storage must not be constructed."));
            services.RemoveAll<InternalAudioTokens>();
            services.AddSingleton(_ => { Interlocked.Increment(ref tokens); return new InternalAudioTokens(TimeProvider.System); });
            services.AddDbContext<DocthecaDbContext>(options => options.AddInterceptors(sql));
        });
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            Guid parse; Guid node; Guid child; Guid moved;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                var nodes = await db.DocumentStructureNodes.Where(n => seed.Files.Contains(n.Parse!.DocumentFileId)).ToListAsync();
                var first = nodes.Single(n => n.ParseId == db.DocumentParses.Where(p => p.DocumentFileId == seed.Files[0] && p.Status == DocumentParseStatus.Parsed).Select(p => p.Id).Single());
                parse = first.ParseId; node = first.Id; child = Guid.NewGuid();
                db.DocumentStructureNodes.Add(new() { Id = child, ParseId = parse, LevelId = first.LevelId, ParentId = node, Name = "child" });
                var childAudio = Audio(parse, child, "folder_name_mismatch"); moved = childAudio.Id;
                db.DocumentStructureAudio.AddRange(Audio(parse, node), childAudio, Audio(parse, null), Audio(parse, Guid.NewGuid(), "node_removed"));
                var second = nodes.Single(n => n.ParseId == db.DocumentParses.Where(p => p.DocumentFileId == seed.Files[1]).Select(p => p.Id).Single());
                db.DocumentStructureAudio.Add(Audio(second.ParseId, second.Id));
                var third = nodes.Single(n => n.ParseId == db.DocumentParses.Where(p => p.DocumentFileId == seed.Files[2]).Select(p => p.Id).Single());
                var foreignLevel = await db.StructureLevels.Where(l => l.DefinitionId == seed.Definitions[1]).Select(l => l.Id).FirstAsync();
                var foreignDefinitionNode = new DocumentStructureNodeEntity { ParseId = third.ParseId, LevelId = foreignLevel, Name = "foreign-definition" };
                db.DocumentStructureNodes.Add(foreignDefinitionNode);
                db.DocumentStructureAudio.Add(Audio(third.ParseId, foreignDefinitionNode.Id));
                var old = Guid.NewGuid();
                db.DocumentParses.Add(new() { Id = old, DocumentFileId = seed.Files[2], Status = DocumentParseStatus.Parsed, ParsedAt = null });
                await db.DocumentParses.Where(p => p.Id == third.ParseId).ExecuteUpdateAsync(set => set.SetProperty(p => p.ParsedAt, DateTimeOffset.UtcNow));
                var oldNode = new DocumentStructureNodeEntity { ParseId = old, LevelId = first.LevelId, Name = "old" };
                db.DocumentStructureNodes.Add(oldNode);
                db.DocumentStructureAudio.AddRange(Audio(third.ParseId, null), Audio(third.ParseId, Guid.NewGuid(), "node_removed"),
                    Audio(third.ParseId, node), Audio(old, oldNode.Id));
                await db.SaveChangesAsync();
            }
            var prefix = $"{Documents}?structureDefinitionId={seed.Definitions[0]}";
            sql.Active = true;
            var filtered = await TraverseAsync(client, prefix + "&subject=Math&grade=G9&hasAudio=TrUe&pageSize=1");
            Assert.Equal(seed.Files.Take(2).Order(), filtered.Select(x => x.GetProperty("id").GetGuid()));
            Assert.Equal(2, filtered.Single(x => x.GetProperty("id").GetGuid() == seed.Files[0]).GetProperty("audioCount").GetInt32());
            Assert.All(filtered, x => Assert.True(x.GetProperty("audioCount").GetInt32() > 0));
            // Each page is exactly one definition-existence SELECT and one bounded SQL projection.
            Assert.Equal(4, sql.Commands.Count);
            Assert.All(sql.Commands.Where(s => s.Contains("document_structure_audio", StringComparison.Ordinal)), s =>
            {
                Assert.Contains("EXISTS", s);
                Assert.Contains("LIMIT", s);
                Assert.Contains("count(*)", s);
                Assert.DoesNotContain("object_key", s);
                Assert.DoesNotContain("source_path", s);
            });
            sql.Active = false;
            var all = await TraverseAsync(client, prefix + "&pageSize=1");
            var falseItems = await TraverseAsync(client, prefix + "&hasAudio=FaLsE&pageSize=2");
            Assert.Equal(all.Select(x => x.ToString()), falseItems.Select(x => x.ToString()));
            Assert.Equal(0, all.Single(x => x.GetProperty("id").GetGuid() == seed.Files[2]).GetProperty("audioCount").GetInt32());
            Assert.Equal(0, all.Single(x => x.GetProperty("id").GetGuid() == seed.Files[3]).GetProperty("audioCount").GetInt32());
            async Task<(int Root, int Child)> Tree()
            {
                using var tree = JsonDocument.Parse(await client.GetStringAsync($"{Documents}/{seed.Files[0]}/structure"));
                var root = tree.RootElement.GetProperty("data").GetProperty("nodes")[0];
                var text = tree.RootElement.ToString();
                Assert.DoesNotContain("canary", text); Assert.DoesNotContain("playback", text); Assert.DoesNotContain("token", text);
                return (root.GetProperty("audioCount").GetInt32(), root.GetProperty("children")[0].GetProperty("audioCount").GetInt32());
            }
            Assert.Equal((1, 1), await Tree());
            Assert.Equal(0, tokens);
            using var unavailable = await client.GetAsync($"/internal/v1/structure-nodes/{node}/audio");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await db.DocumentStructureAudio.Where(a => a.Id == moved).ExecuteUpdateAsync(set => set.SetProperty(a => a.NodeId, (Guid?)node));
            }
            Assert.Equal((2, 0), await Tree());
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await db.DocumentStructureAudio.Where(a => a.Id == moved).ExecuteUpdateAsync(set => set.SetProperty(a => a.NodeId, (Guid?)null));
                await db.DocumentStructureAudio.Where(a => a.ParseId == parse && a.NodeId == node).ExecuteDeleteAsync();
            }
            Assert.Equal((0, 0), await Tree());
            var remaining = await TraverseAsync(client, prefix + "&hasAudio=true");
            Assert.Equal(seed.Files[1], Assert.Single(remaining).GetProperty("id").GetGuid());
        }
        finally { sql.Active = false; await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task AudioCursors_KeepLiteralOldV1IdentityAndRejectCrossFilterReuse()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                foreach (var node in await db.DocumentStructureNodes.Where(n => seed.Matching.Contains(n.Parse!.DocumentFileId)).ToListAsync())
                    db.DocumentStructureAudio.Add(Audio(node.ParseId, node.Id));
                await db.SaveChangesAsync();
            }
            var prefix = $"{Documents}?structureDefinitionId={seed.Definitions[0]}&pageSize=1";
            var firstId = seed.Files.Take(5).Min();
            // This is the exact pre-change serialized v1 identity, independent of the new parser.
            var legacyIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{{\"catalog\":\"documents\",\"definition\":\"{seed.Definitions[0]}\",\"subject\":null,\"grade\":null}}")));
            var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"v\":1,\"after\":\"{firstId}\",\"filter\":\"{legacyIdentity}\"}}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var old = await client.GetAsync(prefix + "&pageToken=" + legacy);
            using var noFilter = await client.GetAsync(prefix + "&hasAudio=false&pageToken=" + legacy);
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
            Assert.Equal(await old.Content.ReadAsStringAsync(), await noFilter.Content.ReadAsStringAsync());
            using var first = JsonDocument.Parse(await client.GetStringAsync(prefix));
            Assert.Equal(legacy, first.RootElement.GetProperty("data").GetProperty("nextPageToken").GetString());
            using var audio = JsonDocument.Parse(await client.GetStringAsync(prefix + "&hasAudio=true"));
            var audioToken = audio.RootElement.GetProperty("data").GetProperty("nextPageToken").GetString();
            Assert.NotNull(audioToken);
            foreach (var url in new[] { prefix + "&hasAudio=true&pageToken=" + legacy, prefix + "&pageToken=" + audioToken, prefix + "&hasAudio=false&pageToken=" + audioToken })
            {
                using var invalid = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal("{\"success\":false,\"errorCode\":\"DOCTHECA_INTERNAL_INVALID\",\"message\":\"Invalid internal API request.\"}", await invalid.Content.ReadAsStringAsync());
            }
        }
        finally { await CleanupAsync(factory, seed); }
    }

    [Fact]
    public async Task AudioCounts_UseTheCatalogSnapshotDuringConcurrentLinkChanges()
    {
        using var seedFactory = Factory(); var seed = await SeedAsync(seedFactory);
        var pause = new SnapshotPause();
        using var factory = Factory(services: s => s.AddDbContext<DocthecaDbContext>(o => o.AddInterceptors(pause)));
        using var client = factory.CreateClient();
        try
        {
            pause.Active = true;
            var reading = client.GetStringAsync($"{Documents}?structureDefinitionId={seed.Definitions[0]}&hasAudio=true");
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var scope = seedFactory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                var node = await db.DocumentStructureNodes.FirstAsync(n => n.Parse!.DocumentFileId == seed.Files[0]);
                db.DocumentStructureAudio.Add(Audio(node.ParseId, node.Id)); await db.SaveChangesAsync();
            }
            pause.Resume.TrySetResult();
            using var before = JsonDocument.Parse(await reading);
            Assert.Empty(before.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
            var after = await TraverseAsync(client, $"{Documents}?structureDefinitionId={seed.Definitions[0]}&hasAudio=true");
            Assert.Equal(1, Assert.Single(after).GetProperty("audioCount").GetInt32());
        }
        finally { pause.Resume.TrySetResult(); await CleanupAsync(seedFactory, seed); }
    }

    private sealed class CatalogSql : DbCommandInterceptor
    {
        public bool Active;
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Active) { Assert.StartsWith("SELECT", command.CommandText.TrimStart()); Commands.Add(command.CommandText); }
            return base.ReaderExecutingAsync(command, data, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.False(Active, "Audio discovery must not write.");
            return base.NonQueryExecutingAsync(command, data, result, cancellationToken);
        }
    }
}
