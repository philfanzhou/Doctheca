using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using Xunit;

namespace Doctheca.Tests.Structure;

/// <summary>
/// Tree-save validation (issue #89): every invalid tree is rejected with a field-level reason and
/// never reaches the repository, so the stored tree stays unchanged.
/// </summary>
public class DocumentStructureServiceTests
{
    private static readonly Guid ParseId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();
    private static readonly Guid DefinitionId = Guid.NewGuid();
    private static readonly Guid OtherDefinitionLevel = Guid.NewGuid();

    // Two configured levels: a top level and its child. Names are arbitrary data.
    private static readonly StructureLevelModel Top = new() { Id = Guid.NewGuid(), DefinitionId = DefinitionId, Name = "L1", SortOrder = 0 };
    private static readonly StructureLevelModel Child = new() { Id = Guid.NewGuid(), DefinitionId = DefinitionId, Name = "L2", ParentLevelId = Top.Id, SortOrder = 1 };

    private readonly Mock<IDocumentStructureRepository> _structure = new();
    private readonly Mock<IDocumentFileRepository> _files = new();
    private readonly DocumentStructureService _service;
    private List<DocumentStructureNodeModel>? _replaced;

    public DocumentStructureServiceTests()
    {
        var parses = new Mock<IDocumentParseRepository>();
        parses.Setup(r => r.GetByIdAsync(ParseId)).ReturnsAsync(new DocumentParseModel { Id = ParseId, DocumentFileId = FileId });
        _files.Setup(r => r.GetByIdAsync(FileId)).ReturnsAsync(new DocumentFileModel { Id = FileId, FileName = "book.pdf", StructureDefinitionId = DefinitionId });
        var blocks = new Mock<IDocumentParseBlockRepository>();
        blocks.Setup(r => r.GetSequencesAsync(ParseId)).ReturnsAsync(Enumerable.Range(0, 100).ToHashSet());
        var definitions = new Mock<IStructureDefinitionRepository>();
        definitions.Setup(r => r.GetByIdAsync(DefinitionId)).ReturnsAsync(new StructureDefinitionModel
        {
            Id = DefinitionId,
            Name = "D",
            Levels = [Top, Child],
        });
        _structure.Setup(r => r.GetByParseIdAsync(ParseId)).ReturnsAsync(() => _replaced ?? []);
        _structure.Setup(r => r.GetForeignNodeIdsAsync(ParseId, It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);
        _structure.Setup(r => r.ReplaceAsync(ParseId, It.IsAny<IReadOnlyList<DocumentStructureNodeModel>>()))
            .Callback<Guid, IReadOnlyList<DocumentStructureNodeModel>>((_, nodes) => _replaced = nodes.ToList())
            .ReturnsAsync(true);

        _service = new DocumentStructureService(parses.Object, _files.Object, blocks.Object, _structure.Object,
            definitions.Object, NullLogger<DocumentStructureService>.Instance);
    }

    private static StructureNodeInput Node(Guid level, string name, int start, int end, params StructureNodeInput[] children) =>
        new(null, level, name, start, end, children);

    [Fact]
    public async Task ValidTree_IsFlattenedParentsFirstWithSiblingOrderByStart()
    {
        var result = await _service.SaveTreeAsync(ParseId,
        [
            Node(Top.Id, "B", 50, 99, Node(Child.Id, "B2", 80, 99), Node(Child.Id, "B1", 50, 79)),
            Node(Top.Id, "A", 0, 49, Node(Child.Id, "A1", 0, 49)),
        ]);

        result.Status.Should().Be(StructureResultStatus.Ok);
        _replaced!.Select(n => n.Name).Should().Equal("A", "A1", "B", "B1", "B2");
        _replaced!.Single(n => n.Name == "B").SortOrder.Should().Be(1);
        _replaced!.Single(n => n.Name == "B1").SortOrder.Should().Be(0);
        _replaced!.Single(n => n.Name == "B2").ParentId.Should().Be(_replaced!.Single(n => n.Name == "B").Id);
        result.Value!.Nodes.Should().HaveCount(5);
    }

    [Fact]
    public async Task GivenIds_AreKeptStable()
    {
        var id = Guid.NewGuid();
        await _service.SaveTreeAsync(ParseId, [new StructureNodeInput(id, Top.Id, "A", 0, 10, null)]);
        _replaced!.Single().Id.Should().Be(id);
    }

    public static TheoryData<string, StructureNodeInput[], string> InvalidTrees => new()
    {
        { "overlapping siblings", [Node(Top.Id, "A", 0, 20), Node(Top.Id, "B", 20, 30)], "nodes[1].startSequence" },
        { "child outside parent", [Node(Top.Id, "A", 10, 20, Node(Child.Id, "A1", 15, 25))], "nodes[0].children[0].startSequence" },
        { "child level not under parent level", [Node(Top.Id, "A", 0, 20, Node(Top.Id, "A1", 0, 10))], "nodes[0].children[0].levelId" },
        { "root with a child level", [Node(Child.Id, "A", 0, 20)], "nodes[0].levelId" },
        { "level from another definition", [Node(OtherDefinitionLevel, "A", 0, 20)], "nodes[0].levelId" },
        { "missing start sequence", [Node(Top.Id, "A", 500, 510)], "nodes[0].startSequence" },
        { "missing end sequence", [Node(Top.Id, "A", 0, 510)], "nodes[0].endSequence" },
        { "start after end", [Node(Top.Id, "A", 30, 20)], "nodes[0].startSequence" },
        { "empty name", [Node(Top.Id, " ", 0, 20)], "nodes[0].name" },
    };

    [Theory]
    [MemberData(nameof(InvalidTrees))]
    public async Task InvalidTree_IsRejectedWithFieldReasonAndNothingIsWritten(string _, StructureNodeInput[] nodes, string field)
    {
        var result = await _service.SaveTreeAsync(ParseId, nodes);

        result.Status.Should().Be(StructureResultStatus.Invalid);
        result.ErrorCode.Should().Be(StructureErrorCodes.Invalid);
        result.Errors.Should().Contain(error => error.Field == field);
        _structure.Verify(r => r.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DocumentStructureNodeModel>>()), Times.Never);
    }

    [Fact]
    public async Task DuplicateIds_AreRejected()
    {
        var id = Guid.NewGuid();
        var result = await _service.SaveTreeAsync(ParseId,
            [new StructureNodeInput(id, Top.Id, "A", 0, 10, null), new StructureNodeInput(id, Top.Id, "B", 11, 20, null)]);
        result.Status.Should().Be(StructureResultStatus.Invalid);
        result.Errors.Should().Contain(error => error.Field == "nodes[1].id");
    }

    [Fact]
    public async Task NodeIdOfAnotherParse_IsRejected()
    {
        var foreign = Guid.NewGuid();
        _structure.Setup(r => r.GetForeignNodeIdsAsync(ParseId, It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([foreign]);
        var result = await _service.SaveTreeAsync(ParseId, [new StructureNodeInput(foreign, Top.Id, "A", 0, 10, null)]);
        result.Status.Should().Be(StructureResultStatus.Invalid);
        result.Errors.Should().Contain(error => error.Field == "nodes[0].id");
    }

    [Fact]
    public async Task DocumentWithoutDefinition_RejectsNodesButAcceptsAnEmptyTree()
    {
        _files.Setup(r => r.GetByIdAsync(FileId)).ReturnsAsync(new DocumentFileModel { Id = FileId });

        (await _service.SaveTreeAsync(ParseId, [Node(Top.Id, "A", 0, 10)])).Errors
            .Should().Contain(error => error.Field == "nodes");
        (await _service.SaveTreeAsync(ParseId, [])).Status.Should().Be(StructureResultStatus.Ok);
    }

    [Fact]
    public async Task MissingParse_IsNotFound()
    {
        (await _service.SaveTreeAsync(Guid.NewGuid(), [])).Status.Should().Be(StructureResultStatus.NotFound);
        (await _service.GetTreeAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task ChangingDefinition_WhileNodesExist_IsConflict()
    {
        _structure.Setup(r => r.DocumentFileHasNodesAsync(FileId)).ReturnsAsync(true);
        var other = Guid.NewGuid();
        var definitions = new Mock<IStructureDefinitionRepository>();
        definitions.Setup(r => r.GetByIdAsync(other)).ReturnsAsync(new StructureDefinitionModel { Id = other });
        var service = new DocumentStructureService(Mock.Of<IDocumentParseRepository>(), _files.Object,
            Mock.Of<IDocumentParseBlockRepository>(), _structure.Object, definitions.Object,
            NullLogger<DocumentStructureService>.Instance);

        var result = await service.SetDocumentDefinitionAsync(FileId, other);

        result.Status.Should().Be(StructureResultStatus.Conflict);
        result.ErrorCode.Should().Be(StructureErrorCodes.DocumentStructureInUse);
        _files.Verify(r => r.SetStructureDefinitionAsync(It.IsAny<Guid>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task ChoosingAnUnknownDefinition_IsInvalid()
    {
        var result = await _service.SetDocumentDefinitionAsync(FileId, Guid.NewGuid());
        result.Status.Should().Be(StructureResultStatus.Invalid);
        result.Errors.Should().ContainSingle(error => error.Field == "structureDefinitionId");
    }
}
