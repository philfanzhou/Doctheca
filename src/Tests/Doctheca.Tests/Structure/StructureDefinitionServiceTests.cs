using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services.Structure;
using Xunit;

namespace Doctheca.Tests.Structure;

public class StructureDefinitionServiceTests
{
    private readonly Mock<IStructureDefinitionRepository> _repository = new();
    private readonly StructureDefinitionService _service;

    public StructureDefinitionServiceTests()
    {
        _service = new StructureDefinitionService(_repository.Object, NullLogger<StructureDefinitionService>.Instance);
        _repository.Setup(r => r.GetUsedLevelIdsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
    }

    [Fact]
    public async Task Create_StoresLevelsInOrderWithParents()
    {
        StructureDefinitionModel? stored = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<StructureDefinitionModel>()))
            .Callback<StructureDefinitionModel>(model => stored = model)
            .Returns(Task.CompletedTask);
        _repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => stored);
        var top = Guid.NewGuid();

        var result = await _service.CreateAsync(new(" Kind ", [new(top, "First", null), new(null, "Second", top)]));

        result.Status.Should().Be(StructureResultStatus.Ok);
        stored!.Name.Should().Be("Kind");
        stored.Levels.Select(l => (l.Name, l.SortOrder)).Should().Equal(("First", 0), ("Second", 1));
        stored.Levels[1].ParentLevelId.Should().Be(top);
        stored.Levels.Should().OnlyContain(l => l.DefinitionId == stored.Id);
    }

    public static TheoryData<string, StructureDefinitionInput, string> InvalidDefinitions()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        return new()
        {
            { "missing name", new(null, [new(null, "L", null)]), "name" },
            { "no levels", new("D", []), "levels" },
            { "unnamed level", new("D", [new(null, "", null)]), "levels[0].name" },
            { "unknown parent", new("D", [new(null, "L", Guid.NewGuid())]), "levels[0].parentLevelId" },
            { "self parent", new("D", [new(a, "L", a)]), "levels[0].parentLevelId" },
            { "cycle", new("D", [new(a, "A", b, true), new(b, "B", a, true)]), "levels[0].parentLevelId" },
            { "duplicate id", new("D", [new(a, "A", null), new(a, "B", null)]), "levels[1].id" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public async Task Create_RejectsInvalidDefinitions(string _, StructureDefinitionInput input, string field)
    {
        var result = await _service.CreateAsync(input);

        result.Status.Should().Be(StructureResultStatus.Invalid);
        result.Errors.Should().Contain(error => error.Field == field);
        _repository.Verify(r => r.AddAsync(It.IsAny<StructureDefinitionModel>()), Times.Never);
    }

    [Fact]
    public async Task Create_RejectsALevelIdOwnedByAnotherDefinition()
    {
        var foreign = Guid.NewGuid();
        _repository.Setup(r => r.GetDefinitionIdOfLevelAsync(foreign)).ReturnsAsync(Guid.NewGuid());

        var result = await _service.CreateAsync(new("D", [new(foreign, "L", null)]));

        result.Errors.Should().Contain(error => error.Field == "levels[0].id");
    }

    [Fact]
    public async Task Update_RemovingOrReparentingAUsedLevel_IsConflict()
    {
        var id = Guid.NewGuid();
        var top = new StructureLevelModel { Id = Guid.NewGuid(), DefinitionId = id, Name = "T" };
        var used = new StructureLevelModel { Id = Guid.NewGuid(), DefinitionId = id, Name = "U", ParentLevelId = top.Id };
        _repository.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(new StructureDefinitionModel { Id = id, Name = "D", Levels = [top, used] });
        _repository.Setup(r => r.GetDefinitionIdOfLevelAsync(It.IsAny<Guid>())).ReturnsAsync(id);
        _repository.Setup(r => r.GetUsedLevelIdsAsync(id)).ReturnsAsync([used.Id]);

        var removed = await _service.UpdateAsync(id, new("D", [new(top.Id, "T", null)]));
        var reparented = await _service.UpdateAsync(id, new("D", [new(top.Id, "T", null), new(used.Id, "U", null)]));
        var renamed = await _service.UpdateAsync(id, new("D2", [new(top.Id, "T", null), new(used.Id, "Renamed", top.Id)]));

        removed.Status.Should().Be(StructureResultStatus.Conflict);
        removed.ErrorCode.Should().Be(StructureErrorCodes.LevelInUse);
        reparented.Status.Should().Be(StructureResultStatus.Conflict);
        _repository.Verify(r => r.UpdateAsync(It.Is<StructureDefinitionModel>(m => m.Name == "D")), Times.Never);
        renamed.Status.Should().NotBe(StructureResultStatus.Conflict);
    }

    [Fact]
    public async Task Delete_InUse_IsConflict_AndMissing_IsNotFound()
    {
        var id = Guid.NewGuid();
        _repository.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(new StructureDefinitionModel { Id = id });
        _repository.Setup(r => r.GetUsedLevelIdsAsync(id)).ReturnsAsync([Guid.NewGuid()]);

        (await _service.DeleteAsync(id)).Status.Should().Be(StructureResultStatus.Conflict);
        (await _service.DeleteAsync(Guid.NewGuid())).Status.Should().Be(StructureResultStatus.NotFound);
        (await _service.UpdateAsync(Guid.NewGuid(), new("D", [new(null, "L", null)]))).Status.Should().Be(StructureResultStatus.NotFound);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }
}
