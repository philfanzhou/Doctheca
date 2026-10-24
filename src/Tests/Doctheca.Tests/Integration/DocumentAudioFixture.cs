using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Doctheca.Tests.Structure;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>Original generated PCM tones; the official MP3 naming contract remains a separate fixture.</summary>
internal static class DocumentAudioFixture
{
    internal static readonly string[] Paths = StructureAudioMatcherTests.OfficialAudioPaths.Select(p => Path.ChangeExtension(p, ".wav")).ToArray();
    internal static byte[] Wave(int index)
    {
        const int samples = 24000;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000);
        writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * (220 + 7 * index) * i / 8000)));
        return stream.ToArray();
    }
    internal static byte[] Archive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            for (var i = 0; i < Paths.Length; i++) { using var entry = zip.CreateEntry(Paths[i]).Open(); entry.Write(Wave(i)); }
        return stream.ToArray();
    }
    internal sealed record Book(Guid Id, Guid Definition, Guid Unit, Guid Section, JsonNode Tree);
    internal static async Task<JsonNode> Data(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, "API returned HTTP " + (int)response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
    }
    internal static async Task<Book> Create(HttpClient client, bool required = true, bool sections = true)
    {
        var u = Guid.NewGuid(); var s = Guid.NewGuid();
        var definition = await Data(await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Audio " + Guid.NewGuid(), levels = new object[] {
            new { id = u, name = "Unit", parentLevelId = (Guid?)null, required }, new { id = s, name = "Section", parentLevelId = (Guid?)u, required = false } } }));
        var d = Guid.Parse(definition["id"]!.GetValue<string>());
        var created = await Data(await client.PostAsJsonAsync("/admin/document-files", new { fileName = "PEP English Grade 9", subject = "English", grade = "9", structureDefinitionId = d }));
        var id = Guid.Parse(created["id"]!.GetValue<string>());
        var tree = await Data(await client.PutAsJsonAsync($"/admin/document-files/{id}/structure", new { nodes = Enumerable.Range(1, 14).Select(i => new {
            levelId = u, name = "Unit " + i, children = sections ? new[] { new { levelId = s, name = "Section A" }, new { levelId = s, name = "Section B" } } : [] }) }));
        return new(id, d, u, s, tree);
    }
    internal static IEnumerable<JsonNode> Nodes(JsonNode tree) => tree["nodes"]!.AsArray().SelectMany(n => new[] { n! }.Concat(n!["children"]!.AsArray().Select(c => c!)));
    internal static string Target(JsonNode tree, string path)
    {
        var original = path[(path.IndexOf("unit", StringComparison.Ordinal))..];
        var unit = original.StartsWith("unit3/9s04Unit4SectionA", StringComparison.Ordinal) ? 4 : int.Parse(original[4..original.IndexOf('/')]);
        var root = tree["nodes"]![unit - 1]!;
        return (original.Contains("Vocabulary", StringComparison.Ordinal) ? root : root["children"]![original.Contains("SectionA", StringComparison.Ordinal) ? 0 : 1])!["id"]!.GetValue<string>();
    }
}
