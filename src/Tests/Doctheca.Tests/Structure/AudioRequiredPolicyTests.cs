using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;
using FluentAssertions;
using Xunit;

namespace Doctheca.Tests.Structure;

public sealed class AudioRequiredPolicyTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(true, true, true)]
    public void DefinitionDescendants_ApplyEvenWhenNoChildNodeExists(bool rootRequired, bool childRequired, bool rejectsRoot)
    {
        var root = new StructureLevelModel { Id = Guid.NewGuid(), Name = "Volume", Required = rootRequired };
        var child = new StructureLevelModel { Id = Guid.NewGuid(), ParentLevelId = root.Id, Name = "Arbitrary part", Required = childRequired };
        var node = new DocumentStructureNodeModel { Id = Guid.NewGuid(), LevelId = root.Id, Name = "A renamed chapter" };
        var policy = new AudioRequiredPolicy([root, child], new Dictionary<Guid, DocumentStructureNodeModel> { [node.Id] = node });
        (policy.Reason(node.Id) is not null).Should().Be(rejectsRoot);
        (policy.Reason(null) is not null).Should().Be(rootRequired || childRequired);
    }
    [Fact]
    public void SiblingBranches_UseTheirOwnRequiredDescendants_NotMaximumNumericDepth()
    {
        var root = new StructureLevelModel { Id = Guid.NewGuid(), Required = true };
        var a = new StructureLevelModel { Id = Guid.NewGuid(), ParentLevelId = root.Id, Required = false };
        var b = new StructureLevelModel { Id = Guid.NewGuid(), ParentLevelId = root.Id, Required = true };
        var c = new StructureLevelModel { Id = Guid.NewGuid(), ParentLevelId = b.Id, Required = true };
        var nodes = new[] { root, a, b, c }.Select(l => new DocumentStructureNodeModel { Id = Guid.NewGuid(), LevelId = l.Id }).ToDictionary(n => n.Id);
        var policy = new AudioRequiredPolicy([root, a, b, c], nodes);
        foreach (var n in nodes.Values) (policy.Reason(n.Id) is not null).Should().Be(n.LevelId == root.Id || n.LevelId == b.Id);
    }
}
