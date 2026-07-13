using System.Text.Json;
using FluentAssertions;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class MinerUFileParseWorkerTests
{
    private static MinerUParseResult CreateChunkResult(string contentListJson)
    {
        return new MinerUParseResult(
            ZipBytes: Array.Empty<byte>(),
            Markdown: "",
            ContentListJson: contentListJson,
            ContentListV2Json: null,
            ModelJson: null,
            LayoutJson: null,
            Images: new List<ImageMetadata>());
    }

    [Fact]
    public void MergeContentListArrays_EmptyList_ReturnsEmptyArray()
    {
        var result = MinerUFileParseWorker.MergeContentListArrays(new List<MinerUParseResult>());

        result.Should().Be("[]");
    }

    [Fact]
    public void MergeContentListArrays_SingleChunk_ReturnsSameBlocks()
    {
        var json = """[{"type":"text","text":"hello","page_id":0}]""";
        var chunks = new List<MinerUParseResult> { CreateChunkResult(json) };

        var result = MinerUFileParseWorker.MergeContentListArrays(chunks);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("text").GetString().Should().Be("hello");
    }

    [Fact]
    public void MergeContentListArrays_MultipleChunks_MergesAllBlocks()
    {
        var json1 = """[{"type":"text","text":"block1","page_id":0}]""";
        var json2 = """[{"type":"text","text":"block2","page_id":0},{"type":"image","page_id":1}]""";
        var chunks = new List<MinerUParseResult>
        {
            CreateChunkResult(json1),
            CreateChunkResult(json2),
        };

        var result = MinerUFileParseWorker.MergeContentListArrays(chunks);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetArrayLength().Should().Be(3);
        doc.RootElement[0].GetProperty("text").GetString().Should().Be("block1");
        doc.RootElement[1].GetProperty("text").GetString().Should().Be("block2");
        doc.RootElement[2].GetProperty("type").GetString().Should().Be("image");
    }

    [Fact]
    public void MergeContentListArrays_SkipsMalformedJson()
    {
        var validJson = """[{"type":"text","text":"ok"}]""";
        var malformedJson = "{invalid json";
        var chunks = new List<MinerUParseResult>
        {
            CreateChunkResult(validJson),
            CreateChunkResult(malformedJson),
        };

        var result = MinerUFileParseWorker.MergeContentListArrays(chunks);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("text").GetString().Should().Be("ok");
    }

    [Fact]
    public void MergeContentListArrays_SkipsEmptyOrNullContentList()
    {
        var validJson = """[{"type":"text","text":"keep"}]""";
        var chunks = new List<MinerUParseResult>
        {
            CreateChunkResult(validJson),
            CreateChunkResult("[]"),
            CreateChunkResult(""),
            CreateChunkResult(null!),
        };

        var result = MinerUFileParseWorker.MergeContentListArrays(chunks);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void MergeContentListArrays_PreservesBlockFields()
    {
        var json = """[{"type":"text","text":"hello","page_id":2,"block_id":"abc123","extra":"field"}]""";
        var chunks = new List<MinerUParseResult> { CreateChunkResult(json) };

        var result = MinerUFileParseWorker.MergeContentListArrays(chunks);

        using var doc = JsonDocument.Parse(result);
        var block = doc.RootElement[0];
        block.GetProperty("type").GetString().Should().Be("text");
        block.GetProperty("text").GetString().Should().Be("hello");
        block.GetProperty("page_id").GetInt32().Should().Be(2);
        block.GetProperty("block_id").GetString().Should().Be("abc123");
        block.GetProperty("extra").GetString().Should().Be("field");
    }
}
