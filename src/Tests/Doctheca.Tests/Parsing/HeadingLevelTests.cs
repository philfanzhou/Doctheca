using System.Globalization;
using Doctheca.Service.Parsing;
using FluentAssertions;
using Xunit;

namespace Doctheca.Tests.Parsing;

public sealed class HeadingLevelTests
{
    [Theory]
    [MemberData(nameof(HeadingLevelCases.All), MemberType = typeof(HeadingLevelCases))]
    public void Classification_UsesCanonicalTypeAndInvariantPositiveInt32(string? type, string? subtype, int expected)
    {
        // The answer must not follow the process's parsing locale.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            StructaDocParseResultSync.ExtractTextLevel(type, subtype).Should().Be(expected);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
