using FluentAssertions;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;
using Xunit;

namespace Doctheca.Tests.Structure;

public class StructurePathResolverTests
{
    [Fact]
    public void Resolve_ReturnsDeepestNodeWithAncestorsAndReadablePath()
    {
        var unit = new DocumentStructureNodeModel { Id = Guid.NewGuid(), Name = "Unit 3", StartSequence = 10, EndSequence = 50 };
        var section = new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParentId = unit.Id, Name = "Section B", StartSequence = 30, EndSequence = 50 };
        var resolver = new StructurePathResolver([section, unit]);

        var inSection = resolver.Resolve(40)!;
        inSection.NodeIds.Should().Equal(unit.Id, section.Id);
        inSection.Path.Should().Be("Unit 3 > Section B");

        var unitOnly = resolver.Resolve(12)!;
        unitOnly.NodeIds.Should().Equal(unit.Id);
        unitOnly.Path.Should().Be("Unit 3");

        resolver.Resolve(5).Should().BeNull();
        resolver.Resolve(null).Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersTheChildWhenItSpansItsWholeParent()
    {
        var parent = new DocumentStructureNodeModel { Id = Guid.NewGuid(), Name = "P", StartSequence = 0, EndSequence = 9 };
        var child = new DocumentStructureNodeModel { Id = Guid.NewGuid(), ParentId = parent.Id, Name = "C", StartSequence = 0, EndSequence = 9 };

        new StructurePathResolver([parent, child]).Resolve(3)!.Path.Should().Be("P > C");
    }
}
