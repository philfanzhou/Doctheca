using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Doctheca.Domain.Models;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service.InternalApi;

internal static class InternalCatalogInput
{
    internal static bool TryRead(IQueryCollection input, bool documents, out InternalCatalogQuery query, out string identity)
    {
        query = new(20, null, null, null, null);
        identity = "";
        var allowed = documents ? new[] { "pageSize", "pageToken", "structureDefinitionId", "subject", "grade", "hasAudio" }
            : new[] { "pageSize", "pageToken" };
        if (input.Any(item => !allowed.Contains(item.Key, StringComparer.Ordinal) || item.Value.Count != 1)) return false;
        var size = 20;
        if (input.TryGetValue("pageSize", out var pageSize) &&
            (!int.TryParse(pageSize.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > 100)) return false;
        var subject = Normalize(input["subject"].ToString());
        var grade = Normalize(input["grade"].ToString());
        if (subject?.Length > 100 || grade?.Length > 100) return false;
        Guid? definition = null;
        var definitionText = Normalize(input["structureDefinitionId"].ToString());
        if (definitionText is not null)
        {
            if (!Guid.TryParseExact(definitionText, "D", out var id)) return false;
            definition = id;
        }
        var hasAudio = false;
        if (input.TryGetValue("hasAudio", out var audio))
        {
            if (string.Equals(audio.ToString(), "true", StringComparison.OrdinalIgnoreCase)) hasAudio = true;
            else if (!string.Equals(audio.ToString(), "false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        // Keep the original v1 identity byte-for-byte for omitted/false filters.
        var normalized = hasAudio
            ? JsonSerializer.Serialize(new { catalog = "documents", definition, subject, grade, hasAudio = true })
            : JsonSerializer.Serialize(new { catalog = documents ? "documents" : "definitions", definition, subject, grade });
        identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        Guid? after = null;
        if (input.TryGetValue("pageToken", out var token))
        {
            if (!TryDecode(token.ToString(), identity, out var id)) return false;
            after = id;
        }
        query = new(size, after, definition, subject, grade, hasAudio);
        return true;
    }

    internal static string Encode(Guid after, string identity) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, after, filter = identity }));

    private static bool TryDecode(string value, string identity, out Guid after)
    {
        after = default;
        if (value.Length is 0 or > 2048 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        try
        {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            if (Base64Url(bytes) != value) return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var fields = root.EnumerateObject().Select(field => field.Name).ToArray();
            if (fields.Length != 3 || fields.Distinct().Count() != 3 ||
                !fields.Order().SequenceEqual(new[] { "after", "filter", "v" })) return false;
            return root.GetProperty("v").ValueKind == JsonValueKind.Number && root.GetProperty("v").TryGetInt32(out var version) && version == 1 &&
                root.GetProperty("after").ValueKind == JsonValueKind.String && Guid.TryParseExact(root.GetProperty("after").GetString(), "D", out after) &&
                root.GetProperty("filter").ValueKind == JsonValueKind.String && root.GetProperty("filter").GetString() == identity;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return false; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string? Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
