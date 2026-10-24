using Doctheca.Tests.Structure;
using Doctheca.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class AudioConfigurationStartupTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    [Theory]
    [MemberData(nameof(DocumentAudioConfigurationTests.InvalidSettings), MemberType = typeof(DocumentAudioConfigurationTests))]
    public void InvalidTunablesRefuseStartupEvenWithoutS3(string key, string value)
    {
        using var factory = CreateFactory(settings: new Dictionary<string, string?> { [key] = value, ["Oss:InternalEndpoint"] = "" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(key, error.Message);
        Assert.DoesNotContain("configuration-canary", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("false", null)]
    [InlineData(null, "false")]
    [InlineData("obsolete-internal-canary", "obsolete-audio-canary")]
    public async Task ObsoleteKeysAreIgnoredWithOneNamesOnlyWarning(string? internalValue, string? audioValue)
    {
        var capture = new RequestScopeCapture();
        var settings = new Dictionary<string, string?>();
        if (internalValue is not null) settings["InternalApi:Enabled"] = internalValue;
        if (audioValue is not null) settings["DocumentAudio:Enabled"] = audioValue;
        using var factory = CreateFactory(settings: settings,
            configureTestServices: services => services.AddSingleton<ILoggerProvider>(capture));
        using var client = factory.CreateClient();
        Assert.True((await client.GetAsync("/internal/v1/documents")).IsSuccessStatusCode);
        Assert.True(factory.Services.GetRequiredService<DocumentAudioStorageStatus>().Available);
        var warnings = capture.Logs.Where(log => log.Message.Contains("DOCTHECA_OBSOLETE_CONFIGURATION", StringComparison.Ordinal)).ToList();
        if (internalValue is null && audioValue is null) Assert.Empty(warnings);
        else
        {
            var warning = Assert.Single(warnings).Message;
            Assert.Equal(internalValue is not null, warning.Contains("InternalApi:Enabled", StringComparison.Ordinal));
            Assert.Equal(audioValue is not null, warning.Contains("DocumentAudio:Enabled", StringComparison.Ordinal));
        }
        Assert.All(capture.Logs, log => Assert.DoesNotContain("obsolete-internal-canary", log.Message));
        Assert.All(capture.Logs, log => Assert.DoesNotContain("obsolete-audio-canary", log.Message));
    }
}
