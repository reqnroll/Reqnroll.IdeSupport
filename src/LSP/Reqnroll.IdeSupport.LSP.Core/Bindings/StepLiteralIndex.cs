using System.Collections.Immutable;
using System.Text;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// A literal-substring prefilter for step-definition matching (issue #471). Narrows the set of
/// bindings actually tried against a step's regex to those whose statically-known literal text
/// fragments are all present in the step text, using a single Aho-Corasick scan over the step
/// text instead of a per-binding regex attempt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is sound (never produces a false negative):</b> every step-definition binding —
/// regardless of whether it was authored as a Cucumber Expression, a plain regex, or derived from
/// a method name with no explicit expression — is reduced to a compiled <see cref="Regex"/> by
/// the time it reaches this index (both the connector's reflection-based discovery and the
/// Roslyn-based syntax discovery guarantee this; see <see cref="ProjectStepDefinitionBinding.Regex"/>).
/// <see cref="ExtractLiteralSegments"/> walks that compiled regex tracking group/class/quantifier
/// depth (<c>(</c>/<c>[</c>/<c>{</c> and their closers), and only characters seen at depth zero —
/// outside every group, character class, and quantifier — that aren't themselves a regex
/// metacharacter or part of an escape sequence are accumulated as literal text. Anything at depth
/// zero that touches an operator (<c>. ^ $ * + ? |</c>) breaks the current run instead of being
/// included, and depth &gt; 0 content is skipped entirely rather than trusted. Because a depth-zero
/// literal run contains no regex syntax at all, it is — by construction — exactly the text
/// <see cref="Regex.Escape(string)"/> would have produced, so any string the full regex matches
/// must contain it verbatim.
/// </para>
/// <para>
/// This is deliberately more conservative than a full regex-structure parse (e.g. an escaped
/// literal like <c>\.</c> breaks the run rather than contributing a literal <c>.</c>, and a
/// literal prefix inside a capturing group like <c>(foo\d+)</c> is not extracted at all) — every
/// choice here trades away a potential literal rather than risk including one that isn't
/// genuinely required. An earlier design reused <c>RegexStepDefinitionExpressionAnalyzer</c>
/// (built for a different purpose, completions sampling) for this classification; that analyzer
/// doesn't track nesting depth, so a non-capturing group containing nested capturing groups —
/// exactly what Cucumber Expression alternation compiles to when combined with another parameter,
/// e.g. <c>(?:(cool)|(bad))</c> — left its closing <c>)</c> unaccounted for and got silently
/// absorbed into trailing text as if it were literal. Reusing a component not designed for
/// correctness-critical classification turned out to be the wrong call; this type is purpose-built
/// for it instead.
/// </para>
/// <para>
/// A binding whose regex resolves to zero depth-zero literal runs (e.g. <c>(.*)</c>, or a
/// Cucumber Expression that is a single <c>{string}</c> placeholder) has no required literals and
/// is unconditionally returned as a candidate for every step — the same behavior as if this index
/// didn't exist, just with no speedup for that specific binding.
/// </para>
/// </remarks>
public sealed class StepLiteralIndex
{
    private const int MinLiteralLength = 3;

    private static readonly StepLiteralIndex EmptyInstance = new(ImmutableArray<ProjectStepDefinitionBinding>.Empty);

    private readonly ImmutableArray<ProjectStepDefinitionBinding> _bindings;
    private readonly int[] _requiredLiteralCounts;
    private readonly List<int>[] _bindingIndexesByLiteralId;
    private readonly List<int> _unfilteredBindingIndexes = new();
    private readonly TrieNode _root = new();

    private StepLiteralIndex(ImmutableArray<ProjectStepDefinitionBinding> bindings)
    {
        _bindings = bindings;
        _requiredLiteralCounts = new int[bindings.Length];
        _root.Fail = _root;

        var literalTextToId = new Dictionary<string, int>(StringComparer.Ordinal);
        var bindingIndexesByLiteralIdBuilder = new List<List<int>>();

        for (var i = 0; i < bindings.Length; i++)
        {
            var literals = ExtractRequiredLiterals(bindings[i]);
            if (literals.Count == 0)
            {
                _unfilteredBindingIndexes.Add(i);
                continue;
            }

            _requiredLiteralCounts[i] = literals.Count;
            foreach (var literal in literals)
            {
                if (!literalTextToId.TryGetValue(literal, out var id))
                {
                    id = literalTextToId.Count;
                    literalTextToId[literal] = id;
                    bindingIndexesByLiteralIdBuilder.Add(new List<int>());
                    Insert(literal, id);
                }

                bindingIndexesByLiteralIdBuilder[id].Add(i);
            }
        }

        _bindingIndexesByLiteralId = bindingIndexesByLiteralIdBuilder.ToArray();
        BuildFailureLinks();
    }

    /// <summary>Builds an index over <paramref name="bindings"/>. Cheap to call for an empty registry.</summary>
    public static StepLiteralIndex Build(ImmutableArray<ProjectStepDefinitionBinding> bindings) =>
        bindings.IsEmpty ? EmptyInstance : new StepLiteralIndex(bindings);

    /// <summary>
    /// Returns the subset of bindings that could possibly match <paramref name="stepText"/>:
    /// every binding with no required literals, plus every binding whose entire required-literal
    /// set was found as a substring of <paramref name="stepText"/>. Callers still need the
    /// binding's own <c>Regex</c>/<c>StepDefinitionType</c> checks — this only narrows, it never
    /// replaces the real match.
    /// </summary>
    public IEnumerable<ProjectStepDefinitionBinding> GetCandidates(string stepText)
    {
        if (_bindingIndexesByLiteralId.Length == 0 || string.IsNullOrEmpty(stepText))
            return _unfilteredBindingIndexes.Select(i => _bindings[i]);

        // Case-insensitive on purpose (issue #471): a binding's regex may be case-insensitive
        // (RegexOptions.IgnoreCase, or an inline (?i) modifier we have no cheap way to detect
        // from the pattern text alone), in which case a literal like "First" is required to match
        // "first" in the step text. Folding both sides to invariant lowercase only ever *widens*
        // the candidate set relative to a case-sensitive comparison, so it can't introduce a false
        // negative for a genuinely case-sensitive binding -- it can only fail to narrow as tightly
        // as it otherwise could.
        var foundLiteralIds = Scan(stepText.ToLowerInvariant());
        if (foundLiteralIds.Count == 0)
            return _unfilteredBindingIndexes.Select(i => _bindings[i]);

        var foundCounts = new Dictionary<int, int>();
        foreach (var literalId in foundLiteralIds)
        foreach (var bindingIndex in _bindingIndexesByLiteralId[literalId])
        {
            foundCounts.TryGetValue(bindingIndex, out var count);
            foundCounts[bindingIndex] = count + 1;
        }

        var candidateIndexes = new List<int>(_unfilteredBindingIndexes);
        foreach (var entry in foundCounts)
            if (entry.Value == _requiredLiteralCounts[entry.Key])
                candidateIndexes.Add(entry.Key);

        return candidateIndexes.Select(i => _bindings[i]);
    }

    // ── Literal extraction ──────────────────────────────────────────────────────

    private static HashSet<string> ExtractRequiredLiterals(ProjectStepDefinitionBinding binding)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (binding.Regex is null)
            return result;

        // Every regex here is wrapped in ^...$ (BuildRegex / GetRegexFromSpecifiedExpression).
        // RegexStepDefinitionExpressionAnalyzer treats an anchor as an operator that poisons the
        // *whole* text segment it's adjacent to, not just itself -- left uncorrected, "^I have "
        // would mark all of "I have " as non-simple even though the anchor is the only actual
        // operator, silently discarding every literal touching either end of the pattern. Strip
        // the wrapper anchors first, mirroring ProjectStepDefinitionBinding.GetSpecifiedExpressionFromRegex's
        // own "remove only one ^/$ from around the regex" logic.
        var pattern = binding.Regex.ToString();
        if (pattern.StartsWith("^", StringComparison.Ordinal))
            pattern = pattern.Substring(1);
        if (pattern.EndsWith("$", StringComparison.Ordinal))
            pattern = pattern.Substring(0, pattern.Length - 1);

        foreach (var segment in ExtractLiteralSegments(pattern))
            if (segment.Length >= MinLiteralLength)
                // Lowercased here so every downstream consumer (dedup, the trie, the scan) works
                // on a consistent case-folded form -- see the case-insensitivity remark on GetCandidates.
                result.Add(segment.ToLowerInvariant());

        return result;
    }

    /// <summary>
    /// Splits <paramref name="pattern"/> into the runs of literal (non-regex-syntax) text that
    /// occur outside every group, character class, and quantifier. See the type's remarks for why
    /// this depth-tracking approach, not a single-pass character scan, is required for soundness.
    /// </summary>
    private static List<string> ExtractLiteralSegments(string pattern)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var i = 0;

        void Flush()
        {
            if (current.Length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
        }

        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '\\')
            {
                // An escape sequence -- could be a literal escape (\.) or a metacharacter class
                // (\d, \w). Break the run rather than trying to distinguish; see remarks.
                Flush();
                i += i + 1 < pattern.Length ? 2 : 1;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                Flush();
                depth++;
                i++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                if (depth > 0)
                    depth--;
                i++;
                continue;
            }

            if (depth == 0)
            {
                if (c is '.' or '^' or '$' or '*' or '+' or '?' or '|')
                    Flush();
                else
                    current.Append(c);
            }

            i++;
        }

        Flush();
        return segments;
    }

    // ── Aho-Corasick trie ────────────────────────────────────────────────────────

    private sealed class TrieNode
    {
        public readonly Dictionary<char, TrieNode> Children = new();
        public TrieNode Fail = null!;
        public List<int>? LiteralIds;
    }

    private void Insert(string literal, int literalId)
    {
        var node = _root;
        foreach (var ch in literal)
        {
            if (!node.Children.TryGetValue(ch, out var next))
            {
                next = new TrieNode();
                node.Children[ch] = next;
            }

            node = next;
        }

        (node.LiteralIds ??= new List<int>()).Add(literalId);
    }

    private void BuildFailureLinks()
    {
        var queue = new Queue<TrieNode>();
        foreach (var child in _root.Children.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var entry in current.Children)
            {
                var ch = entry.Key;
                var child = entry.Value;
                var fail = current.Fail;
                while (fail != _root && !fail.Children.ContainsKey(ch))
                    fail = fail.Fail;

                child.Fail = fail.Children.TryGetValue(ch, out var failChild) && failChild != child
                    ? failChild
                    : _root;

                // Standard Aho-Corasick output-link merge: a match ending at the failure target
                // also "ends" here (e.g. literal "cat" also matches wherever "concat" matches).
                if (child.Fail.LiteralIds is { Count: > 0 })
                    (child.LiteralIds ??= new List<int>()).AddRange(child.Fail.LiteralIds);

                queue.Enqueue(child);
            }
        }
    }

    private HashSet<int> Scan(string text)
    {
        var found = new HashSet<int>();
        var node = _root;

        foreach (var ch in text)
        {
            while (node != _root && !node.Children.ContainsKey(ch))
                node = node.Fail;

            node = node.Children.TryGetValue(ch, out var next) ? next : _root;

            if (node.LiteralIds is { Count: > 0 } ids)
                foreach (var id in ids)
                    found.Add(id);
        }

        return found;
    }
}
