using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class DocumentStructureEndpointTests
{
    [Fact]
    public async Task RequiredPolicy_RoundTripsCreateUpdateNewLevelsAndOldClientReplacement_WithoutChangingTreeOrAudio()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        Guid root = Guid.NewGuid(), child = Guid.NewGuid(), optional = Guid.NewGuid(), branch = Guid.NewGuid();
        var levels = new object[] {
            new { id = root, name = "Root", required = true },
            new { id = child, name = "Child", parentLevelId = root, required = true },
            new { id = optional, name = "Optional", parentLevelId = child },
            new { id = branch, name = "Another root" }
        };
        var created = await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Policy", levels });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        var data = (await ReadData(created))!;
        var id = data["id"]!.GetValue<string>();
        bool[] expected = [true, true, false, false];
        Flags(data).Should().Equal(expected);
        var (file, parse) = await SeedDocumentAsync(factory, 4);
        (await client.PutAsJsonAsync($"/admin/document-files/{file}/metadata", new { structureDefinitionId = id })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/admin/document-parses/{parse}/structure", new { nodes = new[] { Node(root, "Saved root", 0, 3) } })).EnsureSuccessStatusCode();
        Guid audioId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var node = await db.DocumentStructureNodes.SingleAsync(node => node.ParseId == Guid.Parse(parse));
            db.DocumentStructureAudio.Add(new DocumentStructureAudioEntity {
                Id = audioId, ParseId = Guid.Parse(parse), NodeId = node.Id, SourcePath = "synthetic.wav",
                ContentType = "audio/wav", SizeBytes = 10, ObjectKey = "audio/synthetic/test.wav",
                MatchStatus = "matched", LinkSource = "manual"
            });
            await db.SaveOwnedChangesAsync();
            (await db.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == Guid.Parse(id)).OrderBy(level => level.SortOrder)
                .Select(level => level.Required).ToListAsync()).Should().Equal(expected);
        }
        var tree = (await ReadData(await client.GetAsync($"/admin/document-parses/{parse}/structure")))!;
        Flags(tree["definition"]!).Should().Equal(expected);
        var nodesBefore = tree["nodes"]!.ToJsonString();
        var internalDefinitions = (await ReadData(await client.GetAsync("/internal/v1/structure-definitions")))!;
        internalDefinitions.ToJsonString().Should().NotContain("\"required\"");
        var internalTree = (await ReadData(await client.GetAsync($"/internal/v1/documents/{file}/structure")))!;
        internalTree.ToJsonString().Should().NotContain("\"required\"");

        var savedDefinition = (await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!.ToJsonString();
        foreach (var rejectedLevels in new object[][] {
            [new { id = branch, name = "Only remaining branch", required = true }],
            [new { id = branch, name = "Branch", required = true }, new { id = root, name = "Root", parentLevelId = branch, required = true },
             new { id = child, name = "Child", parentLevelId = root, required = true }, new { id = optional, name = "Optional", parentLevelId = child }]
        })
        {
            (await client.PutAsJsonAsync($"/admin/structure-definitions/{id}", new { name = "Rejected", levels = rejectedLevels }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!.ToJsonString().Should().Be(savedDefinition);
        }

        // Add on update, rename and reorder used levels while changing policy only.
        var added = Guid.NewGuid();
        var updated = await client.PutAsJsonAsync($"/admin/structure-definitions/{id}", new { name = "Replaced", levels = new object[] {
            new { id = branch, name = "Another root", required = true },
            new { id = root, name = "Renamed root", required = true },
            new { id = child, name = "Child", parentLevelId = root, required = true },
            new { id = optional, name = "Optional", parentLevelId = child, required = true },
            new { id = added, name = "Added", parentLevelId = optional, required = true }
        }});
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        Flags((await ReadData(updated))!).Should().Equal(true, true, true, true, true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            var raw = await db.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == Guid.Parse(id)).OrderBy(level => level.SortOrder).ToListAsync();
            raw.Should().HaveCount(5).And.OnlyContain(level => level.Required);
            raw.Select(level => level.Id).Should().Equal(branch, root, child, optional, added);
        }
        // A complete old-client PUT omits flags and resets all values to false.
        var omitted = await client.PutAsJsonAsync($"/admin/structure-definitions/{id}", new { name = "Old client", levels = new object[] {
            new { id = branch, name = "Another root" }, new { id = root, name = "Renamed root" },
            new { id = child, name = "Child", parentLevelId = root },
            new { id = optional, name = "Optional", parentLevelId = child }, new { id = added, name = "Added", parentLevelId = optional }
        }});
        omitted.StatusCode.Should().Be(HttpStatusCode.OK);
        Flags((await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!).Should().Equal(false, false, false, false, false);
        var listed = (await ReadData(await client.GetAsync("/admin/structure-definitions")))!.AsArray().Single(row => row!["id"]!.GetValue<string>() == id)!;
        Flags(listed).Should().OnlyContain(flag => !flag);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
            (await db.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == Guid.Parse(id)).ToListAsync())
                .Should().HaveCount(5).And.OnlyContain(level => !level.Required);
            var audio = await db.DocumentStructureAudio.AsNoTracking().SingleAsync(row => row.Id == audioId);
            audio.ObjectKey.Should().Be("audio/synthetic/test.wav");
            audio.NodeId.Should().NotBeNull();
        }
        (await ReadData(await client.GetAsync($"/admin/document-parses/{parse}/structure")))!["nodes"]!.ToJsonString().Should().Be(nodesBefore.Replace("\"levelName\":\"Root\"", "\"levelName\":\"Renamed root\""));
        _searchIndex.IndexedParses.Should().HaveCount(1, "definition policy edits do not reindex");
    }

    [Fact]
    public async Task InvalidRequiredChainsAndStructuralBodies_RejectCreateAndUpdateWithoutWrites()
    {
        using var factory = Factory();
        using var client = Admin(factory);
        Guid root = Guid.NewGuid(), middle = Guid.NewGuid(), leaf = Guid.NewGuid(), branch = Guid.NewGuid();
        var created = await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Preserved", levels = new object[] {
            new { id = root, name = "Root", required = true }, new { id = middle, name = "Middle", parentLevelId = root },
            new { id = leaf, name = "Leaf", parentLevelId = middle }, new { id = branch, name = "Branch" }
        }});
        var id = (await ReadData(created))!["id"]!.GetValue<string>();
        var before = (await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!.ToJsonString();
        var foreign = Guid.NewGuid();
        (await client.PostAsJsonAsync("/admin/structure-definitions", new { name = "Foreign", levels = new[] { new { id = foreign, name = "F" } } })).EnsureSuccessStatusCode();
        var cases = new (object[] Levels, string Field)[] {
            ([new { id = root, name = "Root" }, new { id = middle, name = "Middle", parentLevelId = root, required = true }, new { id = leaf, name = "Leaf", parentLevelId = middle, required = true }], "levels[1].required"),
            ([new { id = root, name = "Root", required = true }, new { id = middle, name = "Middle", parentLevelId = root }, new { id = leaf, name = "Leaf", parentLevelId = middle, required = true }, new { id = branch, name = "Branch" }], "levels[2].required"),
            ([new { id = root, name = "Root", required = true }, new { id = root, name = "Duplicate", required = true }], "levels[1].id"),
            ([new { id = foreign, name = "Foreign", required = true }], "levels[0].id"),
            ([new { id = root, name = "Root", parentLevelId = foreign, required = true }], "levels[0].parentLevelId"),
            ([new { id = root, name = "Root", parentLevelId = root, required = true }], "levels[0].parentLevelId"),
            ([new { id = root, name = "Root", parentLevelId = middle, required = true }, new { id = middle, name = "Middle", parentLevelId = root, required = true }], "levels[0].parentLevelId")
        };
        foreach (var (levels, field) in cases)
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
            {
                using var request = new HttpRequestMessage(method, "/admin/structure-definitions" + (method == HttpMethod.Put ? "/" + id : "")) {
                    Content = new StringContent(ForMethod(method, levels), Encoding.UTF8, "application/json") };
                var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                body["errorCode"]!.GetValue<string>().Should().Be("DOCTHECA_STRUCTURE_INVALID");
                body["errors"]!.AsArray().Should().Contain(error => error!["field"]!.GetValue<string>() == field);
                if (field == "levels[1].required") body["errors"]!.AsArray().Should().Contain(error => error!["field"]!.GetValue<string>() == "levels[2].required");
                (await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!.ToJsonString().Should().Be(before);
            }
        }
        foreach (var json in new[] { "{", "{\"name\":\"Invalid\",\"levels\":[null]}", "{\"name\":\"Invalid\",\"levels\":[{\"name\":\"L\",\"required\":null}]}", "{\"name\":\"Invalid\",\"levels\":[{\"name\":\"L\",\"required\":\"true\"}]}" })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
            {
                using var request = new HttpRequestMessage(method, "/admin/structure-definitions" + (method == HttpMethod.Put ? "/" + id : "")) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            (await ReadData(await client.GetAsync($"/admin/structure-definitions/{id}")))!.ToJsonString().Should().Be(before);
        }
        string ForMethod(HttpMethod method, object[] levels)
        {
            var json = JsonSerializer.Serialize(new { name = "Must not persist", levels });
            if (method == HttpMethod.Post)
                foreach (var known in new[] { root, middle, leaf, branch }) json = json.Replace(known.ToString(), Guid.NewGuid().ToString());
            return json;
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        (await db.StructureDefinitions.CountAsync(row => row.Name == "Must not persist" || row.Name == "Invalid")).Should().Be(0, "invalid creates do not persist definitions");
        (await db.StructureLevels.AsNoTracking().Where(level => level.DefinitionId == Guid.Parse(id)).OrderBy(level => level.SortOrder)
            .Select(level => level.Required).ToListAsync()).Should().Equal(true, false, false, false);
    }

    private static IEnumerable<bool> Flags(JsonNode definition) => definition["levels"]!.AsArray().Select(level => level!["required"]!.GetValue<bool>());
}
