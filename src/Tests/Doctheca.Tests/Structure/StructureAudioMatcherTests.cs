using FluentAssertions;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Services.Structure;
using Xunit;

namespace Doctheca.Tests.Structure;

/// <summary>
/// Pre-filling audio links from folder and file names (issue #90). The textbook case uses the
/// 58 real file paths of the PEP Grade 9 English official audio (names only, no audio content).
/// </summary>
public class StructureAudioMatcherTests
{
    /// <summary>The relative paths of the 58 official audio files, as delivered.</summary>
    internal static readonly string[] OfficialAudioPaths =
    [
        "unit1/9s01Unit1SectionA1b.mp3",
        "unit1/9s02Unit1SectionA1c.mp3",
        "unit1/9s03Unit1SectionA2b.mp3",
        "unit1/9s04Unit1SectionA2c.mp3",
        "unit1/9s05Unit1SectionA3c.mp3",
        "unit1/9s06Unit1SectionB1b.mp3",
        "unit1/9sVocabulary01Unit1.mp3",
        "unit2/9s01Unit2SectionA1b.mp3",
        "unit2/9s02Unit2SectionA1c.mp3",
        "unit2/9s03Unit2SectionA2b.mp3",
        "unit2/9s04Unit2SectionA2c.mp3",
        "unit2/9s05Unit2SectionA3b.mp3",
        "unit2/9s06Unit2SectionB1b.mp3",
        "unit2/9sVocabulary02Unit2.mp3",
        "unit3/9s01Unit3SectionA1b.mp3",
        "unit3/9s02Unit3SectionA1c.mp3",
        "unit3/9s03Unit3SectionA2b.mp3",
        "unit3/9s04Unit4SectionA1e.mp3",
        "unit3/9s05Unit3SectionA3d.mp3",
        "unit3/9s06Unit3SectionB1b.mp3",
        "unit3/9sVocabulary03Unit3.mp3",
        "unit4/9s01Unit4SectionA1b.mp3",
        "unit4/9s02Unit4SectionA1c.mp3",
        "unit4/9s03Unit4SectionA1d.mp3",
        "unit4/9s04Unit4SectionA1e.mp3",
        "unit4/9s05Unit4SectionA2b.mp3",
        "unit4/9s06Unit4SectionA2c.mp3",
        "unit4/9s07Unit4SectionA3c.mp3",
        "unit4/9s08Unit4SectionB1b.mp3",
        "unit4/9sVocabulary04Unit4.mp3",
        "unit5/9s01Unit5SectionA1b.mp3",
        "unit5/9s02Unit5SectionA1c.mp3",
        "unit5/9s03Unit5SectionA2b.mp3",
        "unit5/9s04Unit5SectionA2c.mp3",
        "unit5/9s05Unit5SectionA3c.mp3",
        "unit5/9s06Unit5SectionB1b.mp3",
        "unit5/9sVocabulary05Unit5.mp3",
        "unit6/9s01Unit6SectionA1b.mp3",
        "unit6/9s02Unit6SectionA1c.mp3",
        "unit6/9s03Unit6SectionA2b.mp3",
        "unit6/9s04Unit6SectionA2c.mp3",
        "unit6/9s05Unit6SectionA3b.mp3",
        "unit6/9s06Unit6SectionB1b.mp3",
        "unit6/9sVocabulary06Unit6.mp3",
        "unit7/9s01Unit7SectionA1b.mp3",
        "unit7/9s02Unit7SectionA1c.mp3",
        "unit7/9s03Unit7SectionA2b.mp3",
        "unit7/9s04Unit7SectionA2c.mp3",
        "unit7/9s05Unit7SectionA3c.mp3",
        "unit7/9s06Unit7SectionB1b.mp3",
        "unit7/9sVocabulary07Unit7.mp3",
        "unit8/9s01Unit8SectionA1b.mp3",
        "unit8/9s02Unit8SectionA1c.mp3",
        "unit8/9s03Unit8SectionA2b.mp3",
        "unit8/9s04Unit8SectionA2c.mp3",
        "unit8/9s05Unit8SectionA3b.mp3",
        "unit8/9s06Unit8SectionB1b.mp3",
        "unit8/9sVocabulary08Unit8.mp3"
    ];

    private static readonly string[] UnitTitles =
    [
        "Unit 1 The Changing World", "Unit 2 Saying Thank You", "Unit 3 Exploring the World", "Unit 4 Arts and Crafts",
        "Unit 5 Look Into Science!", "Unit 6 Sports and Health", "Unit 7 Making Our World a Better Place", "Unit 8 Media and You",
    ];

    /// <summary>Unit nodes with Section A, Section B and an unrelated Project node; level names are never used.</summary>
    internal static List<DocumentStructureNodeModel> TextbookNodes()
    {
        var nodes = new List<DocumentStructureNodeModel>();
        for (var u = 0; u < UnitTitles.Length; u++)
        {
            var unit = Node(UnitTitles[u], null, u * 50, u * 50 + 49);
            nodes.Add(unit);
            nodes.Add(Node("Section A What happened?", unit.Id, u * 50, u * 50 + 19));
            nodes.Add(Node("Section B", unit.Id, u * 50 + 20, u * 50 + 39));
            nodes.Add(Node("Project", unit.Id, u * 50 + 40, u * 50 + 49));
        }
        return nodes;
    }

    [Fact]
    public void OfficialAudio_LinksSectionsAndVocabularyUnitsAndFlagsTheFolderMismatch()
    {
        var nodes = TextbookNodes();
        var byId = nodes.ToDictionary(node => node.Id);
        var matcher = new StructureAudioMatcher(nodes);
        string PathOf(Guid id) => byId[id].ParentId is { } parent ? $"{byId[parent].Name} > {byId[id].Name}" : byId[id].Name;

        var results = OfficialAudioPaths.ToDictionary(path => path, matcher.Match);

        results.Should().HaveCount(58);
        var flagged = results.Where(result => result.Value.NodeId == null).ToList();
        flagged.Should().ContainSingle().Which.Key.Should().Be("unit3/9s04Unit4SectionA1e.mp3");
        flagged[0].Value.Status.Should().Be(AudioMatchStatus.FolderNameMismatch);

        foreach (var (path, result) in results.Where(result => result.Value.NodeId != null))
        {
            result.Status.Should().Be(AudioMatchStatus.Matched, path);
            var unit = int.Parse(path[4..path.IndexOf('/')]);
            if (path.Contains("Vocabulary"))
            {
                PathOf(result.NodeId!.Value).Should().Be(UnitTitles[unit - 1], path);
            }
            else
            {
                var section = path.Contains("SectionA") ? "Section A What happened?" : "Section B";
                PathOf(result.NodeId!.Value).Should().Be($"{UnitTitles[unit - 1]} > {section}", path);
            }
        }
        results.Count(result => result.Key.Contains("Vocabulary") && result.Value.NodeId != null).Should().Be(8);
        results.Count(result => !result.Key.Contains("Vocabulary") && result.Value.NodeId != null).Should().Be(49);
    }

    [Theory]
    [InlineData("Unit 1 The Changing World", "unit1")]
    [InlineData("Section A", "sectiona")]
    [InlineData("SectionA", "sectiona")]
    [InlineData("Unit 01", "unit1")]
    [InlineData("Chapter IV Results", "chapteriv")]
    [InlineData("Part 2.1 Methods", "part21")]
    [InlineData("Project", "project")]
    [InlineData("1 Introduction", "1introduction")]
    [InlineData("Starter Unit", "starterunit")]
    // Uncased scripts carry no identifier the matcher could recognise; the whole name is the label.
    [InlineData("第一单元 认识世界", "第一单元认识世界")]
    public void Label_IsTheWordsUpToTheFirstIdentifier(string name, string label)
    {
        StructureAudioMatcher.Label(name).Should().Be(label);
    }

    [Theory]
    [InlineData("9s01Unit1SectionA1b", "9 s 1 unit 1 section a 1 b")]
    [InlineData("unit-03 / Section_B", "unit 3 section b")]
    [InlineData("SECTIONA", "sectiona")]
    public void Tokenize_SplitsAtSeparatorsDigitsAndCaseChanges(string text, string tokens)
    {
        string.Join(' ', StructureAudioMatcher.Tokenize(text)).Should().Be(tokens);
    }

    [Fact]
    public void LabelsMatchOnlyAtTokenBoundaries()
    {
        var unit1 = Node("Unit 1", null, 0, 9);
        var unit10 = Node("Unit 10", null, 10, 19);
        var matcher = new StructureAudioMatcher([unit1, unit10]);

        matcher.Match("Unit10.mp3").NodeId.Should().Be(unit10.Id);
        matcher.Match("unit_1_track.mp3").NodeId.Should().Be(unit1.Id);
        matcher.Match("UNIT 01.mp3").NodeId.Should().Be(unit1.Id);
        matcher.Match("Unit2.mp3").Status.Should().Be(AudioMatchStatus.NoMatch);
        // A lone node is still not matched by its level word alone.
        new StructureAudioMatcher([Node("Unit 2", null, 0, 9)]).Match("unit1/a.mp3").Status.Should().Be(AudioMatchStatus.NoMatch);
    }

    [Fact]
    public void FolderAndNameMustAgree_TheDeeperOneWins()
    {
        var unit = Node("Unit 1", null, 0, 49);
        var section = Node("Section B", unit.Id, 20, 39);
        var other = Node("Unit 2", null, 50, 99);
        var matcher = new StructureAudioMatcher([unit, section, other]);

        matcher.Match("unit1/SectionB.mp3").Should().Be(new AudioMatchResult(section.Id, AudioMatchStatus.Matched));
        matcher.Match("unit1/Unit1SectionB.mp3").Should().Be(new AudioMatchResult(section.Id, AudioMatchStatus.Matched));
        matcher.Match("Unit1 SectionB/track.mp3").Should().Be(new AudioMatchResult(section.Id, AudioMatchStatus.Matched));
        matcher.Match("unit1/track.mp3").Should().Be(new AudioMatchResult(unit.Id, AudioMatchStatus.Matched));
        matcher.Match("unit2/Unit1SectionB.mp3").Should().Be(new AudioMatchResult(null, AudioMatchStatus.FolderNameMismatch));
        matcher.Match("audio/track.mp3").Should().Be(new AudioMatchResult(null, AudioMatchStatus.NoMatch));
        // A child alone does not match without its parent.
        matcher.Match("SectionB.mp3").Should().Be(new AudioMatchResult(null, AudioMatchStatus.NoMatch));
    }

    [Fact]
    public void SiblingsMatchingTheSameName_AreAmbiguous_UnlessOneLabelContainsTheOther()
    {
        var first = Node("Unit 1 Listening", null, 0, 9);
        var second = Node("Unit 1 Review", null, 10, 19);
        new StructureAudioMatcher([first, second]).Match("unit1.mp3").Status.Should().Be(AudioMatchStatus.Ambiguous);

        var general = Node("Section", null, 0, 9);
        var specific = Node("Section A", null, 10, 19);
        new StructureAudioMatcher([general, specific]).Match("SectionA.mp3").NodeId.Should().Be(specific.Id);
        new StructureAudioMatcher([general, specific]).Match("Section.mp3").NodeId.Should().Be(general.Id);
    }

    [Fact]
    public void ThreeLevels_DescendToTheDeepestMatch()
    {
        var chapter = Node("Chapter 2 Method", null, 0, 99);
        var section = Node("Section 2.1 Data", chapter.Id, 0, 49);
        var sub = Node("Subsection a", section.Id, 0, 9);
        var matcher = new StructureAudioMatcher([chapter, section, sub]);

        matcher.Match("Chapter2/Section2.1-Subsection-a.wav").NodeId.Should().Be(sub.Id);
        matcher.Match("chapter 2 section 2.1.mp3").NodeId.Should().Be(section.Id);
    }

    private static DocumentStructureNodeModel Node(string name, Guid? parentId, int start, int end) =>
        new() { Id = Guid.NewGuid(), ParentId = parentId, Name = name, StartSequence = start, EndSequence = end };
}
