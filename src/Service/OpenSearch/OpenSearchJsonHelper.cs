using System.Text.Json;

namespace Ruoyu.Study.DocLibrary.Service.OpenSearch;

internal static class OpenSearchJsonHelper
{
    public static float? TryGetFloat(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetSingle();
        return null;
    }

    public static double? TryGetDouble(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetDouble();
        return null;
    }

    public static string? TryGetString(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var v = el.GetString();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        return null;
    }

    public static int? TryGetInt(JsonElement source, string fieldName)
    {
        if (source.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
