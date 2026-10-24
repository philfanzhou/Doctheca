using System.Net.Http.Json;
using Doctheca.Database;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.StructaDoc;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class DocumentStructureEndpointTests
{
    [Fact]
    public async Task LateWorkerLlmResponse_FillsOnlyCurrentEmptyFieldsAfterManualEndpointUpdate()
    {
        var analysis = new Mock<IDocumentAnalysisService>(); var index = new Mock<ISearchIndexService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analysis.Setup(a => a.AnalyzeMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken ct) => { entered.TrySetResult(); await resume.Task.WaitAsync(ct); return new DocumentMetadataAnalysis { Subject = "Old LLM subject", Grade = "Old LLM grade", Year = "2025" }; });
        index.Setup(i => i.UpdateDocumentFileMetadataAsync(It.IsAny<Guid>(), "English", "9", "2025")).Callback(() => indexed.TrySetResult()).Returns(Task.CompletedTask);
        using var original = AuthoringFactory(true);
        using var factory = original.WithWebHostBuilder(b => b.ConfigureTestServices(services => { services.AddSingleton(analysis.Object); services.RemoveAll<ISearchIndexService>(); services.AddSingleton(index.Object); }));
        using var client = Admin(factory); var source = Guid.NewGuid(); var run = Guid.NewGuid();
        _authoringUpstream.Setup(u => u.UploadDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocDocumentResponse { Id = source });
        _authoringUpstream.Setup(u => u.CreateParseRunAsync(source, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocParseRunResponse { Id = run, DocumentId = source, Status = "succeeded" });
        _authoringUpstream.Setup(u => u.GetParseRunAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync(new StructaDocParseRunResponse { Id = run, DocumentId = source, Status = "succeeded" });
        _authoringUpstream.Setup(u => u.GetAllBlocksAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _authoringUpstream.Setup(u => u.GetPagesAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _authoringUpstream.Setup(u => u.GetAssetsAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _authoringUpstream.Setup(u => u.GetMarkdownAsync(run, It.IsAny<CancellationToken>())).ReturnsAsync("LLM preview");
        var id = (await ReadData(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "Original title" })))!["id"]!.GetValue<string>();
        await ReadData(await client.PostAsync($"/admin/document-files/{id}/source", Pdf()));
        await ReadData(await client.PostAsync($"/admin/document-files/{id}/parse", null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(25));
        try { await ReadData(await client.PutAsJsonAsync($"/admin/document-files/{id}/metadata", new { fileName = "Manual title", subject = "English", grade = "9" })); }
        finally { resume.TrySetResult(); }
        await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var scope = factory.Services.CreateScope(); var file = await scope.ServiceProvider.GetRequiredService<DocthecaDbContext>().DocumentFiles.AsNoTracking().SingleAsync(f => f.Id == Guid.Parse(id));
        file.FileName.Should().Be("Manual title"); file.Subject.Should().Be("English"); file.Grade.Should().Be("9"); file.Year.Should().Be("2025");
    }
}
