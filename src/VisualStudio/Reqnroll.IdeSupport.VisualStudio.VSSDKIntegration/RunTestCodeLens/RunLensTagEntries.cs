#nullable enable

using System.Globalization;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Turns Run-lens tag placements into entries for the shared
/// <see cref="FeatureCodeLensTaggerProvider"/>. Placements come from the symbol tree only; the
/// actual target(s) for a line are resolved lazily, once that line's own
/// <see cref="RunTestCodeLensDataPoint"/> is created.
/// </summary>
internal static class RunLensTagEntries
{
    /// <summary>
    /// The tag entry for one Run-lens placement. The revision key is the scenario's own identity
    /// (name/kind) plus the process-wide target and outcome revisions: when targets are invalidated
    /// or a test run lands, every Run line's descriptor changes, so the CodeLens host re-creates the
    /// out-of-process data points and they pick up the new targets/outcome (see
    /// <see cref="RunTestCodeLensRedirect.InvalidateAll"/> and
    /// <see cref="RunTestCodeLensRedirect.NotifyOutcomesChanged"/>).
    /// </summary>
    internal static FeatureLineLensEntry ToTagEntry(RunTestLensLocation location) =>
        new(location.Line,
            LineLensKinds.Run,
            string.Concat(
                location.Key,
                ";targets-rev:", RunTestCodeLensRedirect.TargetsRevision.ToString(CultureInfo.InvariantCulture),
                ";outcome-rev:", RunTestCodeLensRedirect.OutcomeRevision.ToString(CultureInfo.InvariantCulture)));
}
