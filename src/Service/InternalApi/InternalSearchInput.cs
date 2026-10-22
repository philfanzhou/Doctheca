using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service.InternalApi;

internal sealed record InternalSearchInput(string Query, bool Phrase, Guid? StructureNodeId, int PageSize, string? PageToken)
{
    internal static bool TryRead(IQueryCollection input, out InternalSearchInput query)
    {
        query = new("", false, null, 20, null);
        string[] allowed = ["query", "phrase", "structureNodeId", "pageSize", "pageToken"];
        if (input.Any(item => !allowed.Contains(item.Key, StringComparer.Ordinal) || item.Value.Count != 1)) return false;
        var text = input["query"].ToString().Trim();
        if (text.Length is 0 or > 200) return false;
        var phrase = false;
        if (input.TryGetValue("phrase", out var phraseValue))
        {
            var value = phraseValue.ToString();
            if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            phrase = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        Guid? node = null;
        if (input.TryGetValue("structureNodeId", out var nodeValue))
        {
            var value = nodeValue.ToString();
            if (value.Length != 36 || !Guid.TryParseExact(value, "D", out var id)) return false;
            node = id;
        }
        var size = 20;
        if (input.TryGetValue("pageSize", out var sizeValue))
        {
            var value = sizeValue.ToString();
            if (value.Length == 0 || value.Any(c => c is < '0' or > '9') ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > 100) return false;
        }
        string? token = null;
        if (input.TryGetValue("pageToken", out var tokenValue))
        {
            token = tokenValue.ToString();
            if (!ValidToken(token)) return false;
        }
        query = new(text, phrase, node, size, token);
        return true;
    }

    // Keep the administrative search_after format, but reject invalid input before it can
    // reach the administrative decoder's intentional first-page fallback.
    internal static bool ValidToken(string token)
    {
        if (token.Length is 0 or > 4096) return false;
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (Convert.ToBase64String(bytes) != token) return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2) return false;
            if (root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetDouble(out var score) || !double.IsFinite(score) ||
                root[1].ValueKind != JsonValueKind.String) return false;
            var blockId = root[1].GetString()!;
            return !string.IsNullOrWhiteSpace(blockId) && blockId.Length <= 256 && !blockId.Any(char.IsControl);
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return false; }
    }
}
