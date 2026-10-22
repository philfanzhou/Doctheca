using Doctheca.Domain.Models;
using FluentAssertions;
using Xunit;

namespace Doctheca.Tests.Parsing;

public sealed class DocumentParsePageIdentityTests
{
    [Theory]
    [InlineData("{\"pageNumber\":1}", 0, 1)]
    [InlineData("{\"pageNumber\":9}", 8, 9)]
    [InlineData("{\"pageNumber\":2147483647}", 2147483646, 2147483647)]
    [InlineData("{\"pageNumber\":null}", 0, null)]
    [InlineData("{}", 0, null)]
    [InlineData("null", 0, null)]
    [InlineData("[]", 0, null)]
    [InlineData("broken", 0, null)]
    [InlineData("{\"pageNumber\":\"1\"}", 0, null)]
    [InlineData("{\"pageNumber\":1.0}", 0, null)]
    [InlineData("{\"pageNumber\":1.5}", 0, null)]
    [InlineData("{\"pageNumber\":2147483648}", 0, null)]
    [InlineData("{\"pageNumber\":0}", 0, null)]
    [InlineData("{\"pageNumber\":-1}", 0, null)]
    [InlineData("{\"pageNumber\":2}", 0, null)]
    [InlineData("{\"pageNumber\":1,\"pageNumber\":1}", 0, null)]
    [InlineData("{\"pageNumber\":1}", -1, null)]
    [InlineData("{\"pageNumber\":1}", int.MaxValue, null)]
    public void OnlyNormalizedPhysicalPageMatchingItsDatabaseRowQualifies(string json, int pageId, int? number)
    {
        var parse = new DocumentParseModel { Id = Guid.NewGuid(), StructaDocParseRunId = Guid.NewGuid() };
        var block = new DocumentParseBlockModel { ParseId = parse.Id, PageId = pageId, BlockData = json };
        var key = DocumentParsePageIdentity.FromBlock(parse, block);
        key.Should().Be(number.HasValue ? new DocumentParsePageKey(parse.Id, number.Value) : null);
    }

    [Fact]
    public void LegacyAndForeignParseCannotBorrowPageIdentity()
    {
        var parse = new DocumentParseModel { Id = Guid.NewGuid() };
        var block = new DocumentParseBlockModel { ParseId = parse.Id, PageId = 0, BlockData = "{\"pageNumber\":1}" };
        DocumentParsePageIdentity.FromBlock(parse, block).Should().BeNull();
        parse.StructaDocParseRunId = Guid.NewGuid();
        block.ParseId = Guid.NewGuid();
        DocumentParsePageIdentity.FromBlock(parse, block).Should().BeNull();
    }
}
