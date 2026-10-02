#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// The full result of matching a Gherkin step against the project's step definitions, holding
/// one <see cref="MatchResultItem"/> per candidate (normally one, but more than one when ambiguous).
/// </summary>
public class MatchResult
{
    /// <summary>Sentinel for a step that has no match items and no errors.</summary>
    public static readonly MatchResult NoMatch = new(Array.Empty<MatchResultItem>(), Array.Empty<string>());

    private MatchResult(MatchResultItem[] items, string[] errors)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    /// <summary>The individual match outcomes (defined/ambiguous/undefined) for the step.</summary>
    public MatchResultItem[] Items { get; }

    /// <summary>Any error messages produced while matching, including a combined ambiguous-match message.</summary>
    public string[] Errors { get; }
    /// <summary>True when <see cref="Errors"/> is non-empty.</summary>
    public bool HasErrors => Errors.Any();

    /// <summary>True when at least one item is <see cref="MatchResultType.Undefined"/>.</summary>
    public bool HasUndefined =>
        Items.Any(m => m.Type == MatchResultType.Undefined);

    /// <summary>True when at least one item is <see cref="MatchResultType.Defined"/>.</summary>
    public bool HasDefined =>
        Items.Any(m => m.Type == MatchResultType.Defined);

    /// <summary>True when at least one item is <see cref="MatchResultType.Ambiguous"/>.</summary>
    public bool HasAmbiguous =>
        Items.Any(m => m.Type == MatchResultType.Ambiguous);

    /// <summary>True when more than one item is present (e.g. an ambiguous match).</summary>
    public bool HasMultipleMatches =>
        Items.Length > 1;

    /// <summary>True when exactly one item is present.</summary>
    public bool HasSingleMatch =>
        Items.Length == 1;

    /// <summary>Joins the string representation of each item, one per line.</summary>
    public override string ToString()
    {
        return string.Join(Environment.NewLine, Items.Select(sd => sd.ToString()));
    }

    /// <summary>Returns all <see cref="Errors"/> joined into a single message, or null when there are none.</summary>
    public string? GetErrorMessage()
    {
        if (!HasErrors)
            return null;
        return string.Join(Environment.NewLine, Errors);
    }

    /// <summary>
    /// Builds a <see cref="MatchResult"/> from multiple candidate items, adding a combined
    /// error message when any item is ambiguous.
    /// </summary>
    public static MatchResult CreateMultiMatch(MatchResultItem[] items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        var errors = items.SelectMany(m => m.Errors);
        if (items.Any(m => m.Type == MatchResultType.Ambiguous))
        {
            var ambiguousMatches = items.Where(m => m.Type == MatchResultType.Ambiguous);
            var ambiguousErrorMessage =
                $"Ambiguous steps (use Go To Definition to navigate):{Environment.NewLine}{string.Join(Environment.NewLine, ambiguousMatches.Select(sd => sd.MatchedStepDefinition.ToString()))}";
            errors = errors.Concat(new[] {ambiguousErrorMessage});
        }

        return new MatchResult(items, errors.ToArray());
    }
}
