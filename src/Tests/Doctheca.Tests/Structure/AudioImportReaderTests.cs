using System.IO.Compression;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Doctheca.Domain.Models;
using Doctheca.Service.Audio;
using Xunit;

namespace Doctheca.Tests.Structure;

public sealed class AudioImportReaderTests
{
    [Theory]
    [InlineData("../a.mp3")]
    [InlineData("a/./b.mp3")]
    [InlineData("a//b.mp3")]
    [InlineData("C:/a.mp3")]
    [InlineData("/a.mp3")]
    [InlineData("a\u0001.mp3")]
    public void UnsafePathsAreRejected(string raw) =>
        AudioImportReader.TryNormalizePath(raw, out _, out _).Should().BeFalse();

    [Fact]
    public void InvalidUnicodeAndBoundedPathsAreRejectedWithoutRawErrors()
    {
        foreach (var path in new[] { "\ud800.mp3", new string('a', 256) + ".mp3", string.Join('/', Enumerable.Repeat(new string('a', 200), 6)) })
            AudioImportReader.TryNormalizePath(path, out _, out _).Should().BeFalse();
        AudioImportReader.TryNormalizePath("unit1\\cafe\u0301.mp3", out var normalized, out _).Should().BeTrue();
        normalized.Should().Be("unit1/caf\u00e9.mp3");
    }

    [Theory]
    [InlineData("a.mp3", "ID3xxxxxxxxx")]
    [InlineData("a.m4a", "xxxxftypxxxx")]
    [InlineData("a.wav", "RIFFxxxxWAVE")]
    [InlineData("a.ogg", "OggSxxxxxxxx")]
    [InlineData("a.flac", "fLaCxxxxxxxx")]
    public async Task SupportedSignaturesAreAccepted(string name, string signature)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(signature);
        using var stream = new MemoryStream(bytes);
        using var batch = await AudioImportReader.ReadAsync(Form(new FormFile(stream, 0, bytes.Length, "files", name)), new());
        batch.Errors.Should().BeEmpty();
        batch.Entries.Should().ContainSingle().Which.SizeBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task CancellationStopsValidationAndMalformedPathsFieldsRejectTheBatch()
    {
        using var stream = new MemoryStream("ID3xxxxxxxxx"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "files", "a.mp3");
        using var token = new CancellationTokenSource();
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioImportReader.ReadAsync(Form(file), new(), token.Token));
        var form = new FormCollection(new Dictionary<string, StringValues> { ["paths"] = new(new[] { "a", "b" }) }, new FormFileCollection { file });
        using var batch = await AudioImportReader.ReadAsync(form, new());
        batch.Errors.Should().ContainSingle();
        batch.Entries.Should().BeEmpty();
    }

    [Fact]
    public void InvalidLimitsAreRejectedIncludingMultipartOverflow()
    {
        foreach (var options in new[]
        {
            new DocumentAudioOptions { MaxFileBytes = 0 },
            new DocumentAudioOptions { MaxFileBytes = 100, MaxArchiveBytes = 99 },
            new DocumentAudioOptions { MaxArchiveBytes = long.MaxValue },
        })
            options.Validate().Should().NotBeNull();
        var defaults = new DocumentAudioOptions();
        defaults.MaxFileBytes.Should().Be(20971520);
        defaults.MaxArchiveBytes.Should().Be(209715200);
        DocumentAudioStorageStatus.Evaluate(false, key => key == "PublicBaseUrl" ? null : "configured")
            .Available.Should().BeTrue("import only needs the internal S3 settings");
    }

    private static FormCollection Form(IFormFile file) => new([], new FormFileCollection { file });
}
