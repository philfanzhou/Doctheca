using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class SensitiveDataMaskerTests
{
    [Theory]
    [InlineData("sk-abcdef1234567890", "sk-a****7890")]
    [InlineData("sk-proj-abc123def456", "sk-p****f456")]
    public void MaskApiKey_NormalLength_PreservesHead4AndTail4(string apiKey, string expected)
    {
        Assert.Equal(expected, SensitiveDataMasker.MaskApiKey(apiKey));
    }

    [Theory]
    [InlineData("1234567", "****")]
    [InlineData("12345678", "1234****5678")]
    [InlineData("123456789", "1234****6789")]
    public void MaskApiKey_BoundaryLengths_HandlesCorrectly(string apiKey, string expected)
    {
        Assert.Equal(expected, SensitiveDataMasker.MaskApiKey(apiKey));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MaskApiKey_EmptyOrNull_ReturnsEmpty(string? apiKey, string expected)
    {
        Assert.Equal(expected, SensitiveDataMasker.MaskApiKey(apiKey));
    }
}
