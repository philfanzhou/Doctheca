using System.Globalization;
using System.Text;
using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Services.Structure;

/// <summary>The pre-filled link of one file: the node (null when unlinked) and why.</summary>
public sealed record AudioMatchResult(Guid? NodeId, string Status);

/// <summary>
/// Pre-fills the node of an imported audio file from its folder and file name (issue #90). No
/// level name or file naming convention is known here; only the node names of the marked tree are
/// compared.
/// </summary>
/// <remarks>
/// <para>
/// Names are compared with case, spaces and punctuation ignored, and numbers by value
/// (<c>Unit 01</c> equals <c>Unit1</c>). A name is split into tokens at separators, at
/// letter/digit changes and at lower-to-upper case changes, so <c>9s01Unit1SectionA1b</c> becomes
/// <c>9 s 1 unit 1 section a 1 b</c>.
/// </para>
/// <para>
/// Each node is matched by its <em>label</em>: the words of its name up to and including the
/// first identifier after a word (a number with any following numbers, a single letter, or a
/// Roman numeral), so <c>Unit 1 The Changing World</c> has the label <c>unit1</c> and
/// <c>Section A</c> the label <c>sectiona</c>. A name without such an identifier is its own label.
/// A label matches when it occurs in the compared text starting and ending at token boundaries,
/// so <c>unit1</c> does not match <c>Unit10</c>.
/// </para>
/// <para>
/// Folder and file name are matched separately, each descending from the root nodes to their
/// children and stopping at the deepest node whose children no longer match. When both find a
/// node they must agree (one is the other or its ancestor), and the deeper one is taken; when they
/// point to different branches the file is left unlinked as
/// <see cref="AudioMatchStatus.FolderNameMismatch"/>. From the agreed node the descent continues
/// with folder and name together, so <c>unit1/SectionB.mp3</c> reaches the Section B child. Two matching siblings at any step make the
/// result <see cref="AudioMatchStatus.Ambiguous"/>, unless one sibling's label occurrences all lie
/// inside a longer sibling label's occurrence.
/// </para>
/// </remarks>
public sealed class StructureAudioMatcher
{
    private static readonly HashSet<string> RomanNumerals =
    [
        "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x",
        "xi", "xii", "xiii", "xiv", "xv", "xvi", "xvii", "xviii", "xix", "xx",
    ];

    private readonly ILookup<Guid?, Candidate> _children;
    private readonly Dictionary<Guid, Guid?> _parentOf;

    public StructureAudioMatcher(IReadOnlyCollection<DocumentStructureNodeModel> nodes)
    {
        _parentOf = nodes.ToDictionary(node => node.Id, node => node.ParentId);
        _children = nodes
            .Select(node => new Candidate(node.Id, Label(node.Name)))
            .Where(candidate => candidate.Label.Length > 0)
            .ToLookup(candidate => _parentOf[candidate.Id]);
    }

    /// <summary>Matches a relative path such as <c>unit1/9s01Unit1SectionA1b.mp3</c>.</summary>
    public AudioMatchResult Match(string sourcePath)
    {
        var normalized = sourcePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : normalized[..slash];
        var fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
        var dot = fileName.LastIndexOf('.');
        if (dot > 0) fileName = fileName[..dot];

        var byFolder = Descend(folder, null);
        var byName = Descend(fileName, null);
        if (byFolder.Ambiguous || byName.Ambiguous) return new(null, AudioMatchStatus.Ambiguous);

        Guid? agreed;
        if (byFolder.NodeId is not { } folderNode) agreed = byName.NodeId;
        else if (byName.NodeId is not { } nameNode) agreed = folderNode;
        else if (IsSelfOrAncestor(folderNode, nameNode)) agreed = nameNode;
        else if (IsSelfOrAncestor(nameNode, folderNode)) agreed = folderNode;
        else return new(null, AudioMatchStatus.FolderNameMismatch);
        if (agreed == null) return new(null, AudioMatchStatus.NoMatch);

        // Below the agreed node, folder and name together may name a child neither names alone
        // from the root, as in unit1/SectionB.mp3.
        var deeper = Descend($"{folder}/{fileName}", agreed);
        return deeper.Ambiguous
            ? new(null, AudioMatchStatus.Ambiguous)
            : new(deeper.NodeId, AudioMatchStatus.Matched);
    }

    private (Guid? NodeId, bool Ambiguous) Descend(string text, Guid? from)
    {
        var compared = Tokenized.Of(text);
        var current = from;
        while (true)
        {
            var matches = _children[current]
                .Select(candidate => (candidate, spans: compared.Occurrences(candidate.Label)))
                .Where(match => match.spans.Count > 0)
                .ToList();
            // A sibling whose every occurrence lies inside a longer sibling's occurrence loses,
            // so a node named "Section" does not compete with "Section A" for "SectionA".
            matches = matches
                .Where(match => !matches.Any(other => other.candidate.Label.Length > match.candidate.Label.Length
                    && match.spans.All(span => other.spans.Any(outer => outer.Start <= span.Start && span.End <= outer.End))))
                .ToList();
            if (matches.Count == 0) return (current, false);
            if (matches.Count > 1) return (null, true);
            current = matches[0].candidate.Id;
        }
    }

    private bool IsSelfOrAncestor(Guid ancestor, Guid node)
    {
        for (Guid? cursor = node; cursor is { } id; cursor = _parentOf.GetValueOrDefault(id))
        {
            if (id == ancestor) return true;
        }
        return false;
    }

    /// <summary>The comparable label of a node name (see the class remarks).</summary>
    public static string Label(string name)
    {
        var tokens = Tokenize(name);
        var end = tokens.Count;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (!IsIdentifier(tokens[i]) || IsIdentifier(tokens[i - 1])) continue;
            end = i + 1;
            if (IsNumber(tokens[i]))
            {
                while (end < tokens.Count && IsNumber(tokens[end])) end++;
            }
            break;
        }
        return string.Concat(tokens.Take(end));
    }

    private static bool IsNumber(string token) => char.IsDigit(token[0]);

    private static bool IsIdentifier(string token) =>
        IsNumber(token) || (token.Length == 1 && char.IsLetter(token[0]) && !IsUncased(token[0])) || RomanNumerals.Contains(token);

    /// <summary>Lower-cased tokens; numbers lose their leading zeros.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var previous = '\0';
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                previous = '\0';
                continue;
            }

            if (previous != '\0' && IsBoundary(previous, c)) Flush();
            current.Append(char.ToLowerInvariant(c));
            previous = c;
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            var token = current.ToString();
            current.Clear();
            if (char.IsDigit(token[0]))
            {
                token = token.TrimStart('0');
                if (token.Length == 0) token = "0";
            }
            tokens.Add(token);
        }
    }

    private static bool IsBoundary(char previous, char current)
    {
        if (char.IsDigit(previous) != char.IsDigit(current)) return true;
        if (char.IsLower(previous) && char.IsUpper(current)) return true;
        // Uncased scripts (for example CJK) carry no word separators; every character is a token.
        return IsUncased(previous) || IsUncased(current);
    }

    private static bool IsUncased(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherLetter;

    private sealed record Candidate(Guid Id, string Label);

    private readonly record struct Span(int Start, int End);

    /// <summary>Concatenated tokens and the positions where one token ends and the next begins.</summary>
    private sealed class Tokenized
    {
        private readonly string _text;
        private readonly HashSet<int> _boundaries;

        private Tokenized(string text, HashSet<int> boundaries)
        {
            _text = text;
            _boundaries = boundaries;
        }

        public static Tokenized Of(string text)
        {
            var builder = new StringBuilder();
            var boundaries = new HashSet<int> { 0 };
            foreach (var token in Tokenize(text))
            {
                builder.Append(token);
                boundaries.Add(builder.Length);
            }
            return new Tokenized(builder.ToString(), boundaries);
        }

        public List<Span> Occurrences(string label)
        {
            var spans = new List<Span>();
            for (var start = _text.IndexOf(label, StringComparison.Ordinal);
                 start >= 0;
                 start = _text.IndexOf(label, start + 1, StringComparison.Ordinal))
            {
                if (_boundaries.Contains(start) && _boundaries.Contains(start + label.Length))
                    spans.Add(new Span(start, start + label.Length));
            }
            return spans;
        }
    }
}
