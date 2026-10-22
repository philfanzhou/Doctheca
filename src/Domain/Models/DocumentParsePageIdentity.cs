using System.Text.Json;

namespace Doctheca.Domain.Models;

/// <summary>Distinguishes a known canonical page from the legacy null-page fallback.</summary>
public static class DocumentParsePageIdentity
{
    public static DocumentParsePageKey? FromBlock(DocumentParseModel parse, DocumentParseBlockModel block)
    {
        if (parse.Id != block.ParseId || !parse.StructaDocParseRunId.HasValue
            || parse.StructaDocParseRunId == Guid.Empty || block.PageId < 0 || block.PageId == int.MaxValue
            || string.IsNullOrEmpty(block.BlockData))
            return null;

        try
        {
            using var json = JsonDocument.Parse(block.BlockData);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var fields = json.RootElement.EnumerateObject().Where(property => property.NameEquals("pageNumber")).ToList();
            if (fields.Count != 1 || fields[0].Value.ValueKind != JsonValueKind.Number
                || !fields[0].Value.TryGetInt32(out var number) || number <= 0 || number != block.PageId + 1)
                return null;
            return new(parse.Id, number);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
