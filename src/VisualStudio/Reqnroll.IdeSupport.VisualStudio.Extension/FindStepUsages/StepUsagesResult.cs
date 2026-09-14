using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Three-state result from <see cref="FindStepUsagesService.FindUsagesAsync"/>:
/// <list type="bullet">
///   <item><see cref="NotABinding"/> — caret is not on any step-definition binding; callers show an
///         informational message. (There is no takeover of the built-in Find All References command —
///         Surface 3 in the F14 design doc — that surface was deferred and never implemented.)</item>
///   <item><see cref="IsBinding"/> with <c>Locations.Count == 0</c> — binding present but no matching steps; show "0 usages" window.</item>
///   <item><see cref="IsBinding"/> with <c>Locations.Count > 0</c> — matching feature-file steps; show them in the results window.</item>
/// </list>
/// </summary>
internal sealed class StepUsagesResult
{
    /// <summary>Sentinel: the queried position is not a step-definition binding.</summary>
    public static readonly StepUsagesResult NotABinding = new StepUsagesResult();

    private readonly IReadOnlyList<StepUsageLocation>? _locations;

    // Private constructor for the NotABinding sentinel.
    private StepUsagesResult() { }

    /// <summary>Creates a result for a binding (zero or more usages).</summary>
    public StepUsagesResult(IReadOnlyList<StepUsageLocation> locations)
    {
        _locations = locations;
    }

    /// <summary><see langword="true"/> when the queried position resolved to a step-definition binding.</summary>
    public bool IsBinding => _locations is not null;

    /// <summary>The matching step-usage locations; empty when not a binding or when there are no usages.</summary>
    public IReadOnlyList<StepUsageLocation> Locations =>
        _locations ?? System.Array.Empty<StepUsageLocation>();
}
