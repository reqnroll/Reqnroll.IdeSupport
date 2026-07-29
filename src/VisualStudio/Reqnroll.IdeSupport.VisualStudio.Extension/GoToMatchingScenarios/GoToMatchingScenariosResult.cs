#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;

/// <summary>
/// Parsed result of a <c>reqnroll/goToMatchingScenarios</c> response (issue #373) — the inverse
/// of <see cref="GoToHooks.GoToHooksResult"/>.
/// </summary>
internal sealed class GoToMatchingScenariosResult
{
    /// <summary>Sentinel for a hook binding with no matching scenarios.</summary>
    public static readonly GoToMatchingScenariosResult Empty = new(new List<MatchingScenarioLocation>());

    /// <summary>The matching scenario locations, in server-returned order.</summary>
    public IReadOnlyList<MatchingScenarioLocation> Scenarios { get; }

    /// <summary>Creates a result wrapping the given scenario locations.</summary>
    public GoToMatchingScenariosResult(IReadOnlyList<MatchingScenarioLocation> scenarios)
    {
        Scenarios = scenarios;
    }
}

/// <summary>One scenario matched by the queried hook's scope, returned by the server.</summary>
internal sealed class MatchingScenarioLocation
{
    /// <summary>The feature-file document URI declaring the scenario.</summary>
    public string Uri          { get; }
    /// <summary>0-based start line of the scenario/scenario outline definition.</summary>
    public int    StartLine    { get; }
    /// <summary>0-based start character of the scenario/scenario outline definition.</summary>
    public int    StartChar    { get; }
    /// <summary>The scenario's title, or an empty string if untitled.</summary>
    public string ScenarioName { get; }
    /// <summary>True when this is a Scenario Outline definition.</summary>
    public bool   IsOutline    { get; }

    /// <summary>Creates a scenario location from server-supplied coordinates and metadata.</summary>
    public MatchingScenarioLocation(
        string uri, int startLine, int startChar, string scenarioName, bool isOutline)
    {
        Uri          = uri;
        StartLine    = startLine;
        StartChar    = startChar;
        ScenarioName = scenarioName;
        IsOutline    = isOutline;
    }
}
