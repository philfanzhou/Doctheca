using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Service.Audio;
using Doctheca.Tests.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Doctheca.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class DocumentOwnedAudioMigrationTests(PostgreSqlMigrationFixture postgres, MinioFixture minio) : IClassFixture<MinioFixture>
{
    [Fact]
    public async Task PopulatedUpgradeAndParseDeletion_PreserveStoredKeyAndPlayableObjectChecksum()
    {
        var database = await postgres.CreateDatabaseAsync(); var bucket = "ownership-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            using var context = postgres.CreateContext(database);
            await context.GetService<IMigrator>().MigrateAsync(DocthecaMigrationExecutor.AddRequiredStructureLevelsMigrationId);
            await PageMigrationTests.SeedAllEarlierTablesAsync(context);
            var id = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM document_structure_audio").SingleAsync();
            var key = $"audio/{MigrationGoldenStates.ParseId:N}/{id:N}.wav"; var bytes = PlayableWave.Bytes();
            await context.Database.ExecuteSqlAsync($"UPDATE document_parses SET status='parsed'");
            await context.Database.ExecuteSqlAsync($"UPDATE document_structure_audio SET object_key={key},size_bytes={bytes.LongLength}");
            await minio.PutAsync(bucket, key, bytes);
            await new DocthecaMigrationExecutor(context).ExecuteAsync();
            var settings = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings(bucket)).Build();
            var storage = new S3OssService(settings.GetSection("Oss").Get<OssOptions>()!);
            var store = new StructureAudioStore(() => storage, new(true, null), NullLogger<StructureAudioStore>.Instance);
            var repository = new InternalStructureRepository(new DbContextOptionsBuilder<DocthecaDbContext>().UseNpgsql(postgres.GetConnectionString(database)).Options);
            var node = await context.DocumentStructureNodes.Select(item => item.Id).SingleAsync();
            async Task CheckAsync()
            {
                var membership = await repository.ReadAudioAsync(node, id, default); var audio = membership!.Items.Single();
                audio.ObjectKey.Should().Be(key); audio.ParseId.Should().Be(MigrationGoldenStates.ParseId);
                await using var playback = await store.BufferAsync(audio, bytes.Length, new AudioScratchSpace(), default);
                using var result = new MemoryStream(); await playback.CopyToAsync(result);
                System.Security.Cryptography.SHA256.HashData(result.ToArray()).Should().Equal(System.Security.Cryptography.SHA256.HashData(bytes));
            }
            await CheckAsync(); await new DocumentParseRepository(context).DeleteAsync(MigrationGoldenStates.ParseId); await CheckAsync();
            (await minio.KeysAsync(bucket)).Should().Equal(key);
        }
        finally { await postgres.DropDatabaseAsync(database); }
    }
}
