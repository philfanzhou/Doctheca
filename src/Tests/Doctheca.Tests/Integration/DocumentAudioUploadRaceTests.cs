using System.Net;
using System.Net.Http.Json;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Minio.DataModel.Args;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class StructureAudioEndpointTests
{
    [Theory]
    [InlineData("tighten", 400)] [InlineData("loosen", 200)] [InlineData("node", 404)] [InlineData("document", 404)]
    [InlineData("duplicate", 409)] [InlineData("parse-complete", 200)] [InlineData("parse-delete", 200)]
    public async Task DocumentUpload_StorageIsOutsideOwnerLock_CommitRevalidates_AndLoserNeverDeletesWinner(string action, int status)
    {
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var factory = Factory(configure: services =>
        {
            services.RemoveAll<IOssService>(); services.AddScoped<IOssService>(sp =>
            {
                var actual = new S3OssService(sp.GetRequiredService<IOptions<OssOptions>>().Value); var proxy = new Mock<IOssService>(MockBehavior.Strict);
                proxy.Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), OssBucket.Audio, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns(async (Stream bytes, string name, string type, OssBucket bucket, string? folder, CancellationToken ct) =>
                    {
                        var key = await actual.UploadAsync(bytes, name, type, bucket, folder, ct);
                        if (Interlocked.Increment(ref calls) == 1) { uploaded.TrySetResult(); await release.Task.WaitAsync(ct); }
                        return key;
                    });
                proxy.Setup(x => x.DeleteAsync(It.IsAny<string>())).Returns((string key) => actual.DeleteAsync(key));
                return proxy.Object;
            });
        });
        using var client = Admin(factory); var book = await DocumentAudioFixture.Create(client, required: action != "tighten");
        var route = $"/admin/document-files/{book.Id}/audio";
        MultipartFormDataContent Form()
        {
            var form = Upload(("unit1/SectionA.wav", DocumentAudioFixture.Wave(0)));
            if (action is not ("tighten" or "loosen")) form.Add(new StringContent(DocumentAudioFixture.Target(book.Tree, "unit1/SectionA.wav")), "targetNodeId");
            return form;
        }
        var pending = client.PostAsync(route, Form()); await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (action is "tighten" or "loosen") await SetAudioPolicy(client, book, action == "tighten", false);
            if (action == "node") { book.Tree["nodes"]![0]!["children"]!.AsArray().RemoveAt(0); await DocumentAudioFixture.Data(await client.PutAsJsonAsync($"/admin/document-files/{book.Id}/structure", new { nodes = book.Tree["nodes"] })); }
            if (action == "document") (await client.DeleteAsync($"/admin/document-files/{book.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
            if (action == "duplicate") (await client.PostAsync(route, Form())).StatusCode.Should().Be(HttpStatusCode.OK);
            if (action is "parse-complete" or "parse-delete")
            {
                using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
                await new DocumentAuthoringRepository(db).AttachSourceAsync(book.Id, Guid.NewGuid(), "application/pdf", default);
                var parse = await new DocumentParseRepository(db).AddAsync(new() { DocumentFileId = book.Id });
                if (action == "parse-complete") await new DocumentParseRepository(db).CompleteAsync(parse.Id, Guid.NewGuid(), "new content", [], [], []);
                else await new DocumentParseRepository(db).DeleteAsync(parse.Id);
            }
        }
        finally { release.TrySetResult(); }
        using var response = await pending; ((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
        using var read = factory.Services.CreateScope(); var context = read.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var rows = await context.DocumentStructureAudio.AsNoTracking().Where(a => a.DocumentFileId == book.Id).ToListAsync();
        var keep = status == 200 || action == "duplicate"; rows.Should().HaveCount(keep ? 1 : 0);
        var keys = await minio.KeysAsync(_bucket); keys.Should().HaveCount(keep ? 1 : 0);
        if (keep)
        {
            keys.Should().Equal(rows[0].ObjectKey); rows[0].ParseId.Should().BeNull(); rows[0].DocumentFileId.Should().Be(book.Id);
            using var storage = minio.Client(); using var bytes = new MemoryStream();
            await storage.GetObjectAsync(new GetObjectArgs().WithBucket(_bucket).WithObject(rows[0].ObjectKey).WithCallbackStream(stream => stream.CopyTo(bytes)));
            bytes.ToArray().Should().Equal(DocumentAudioFixture.Wave(0));
        }
    }
}
