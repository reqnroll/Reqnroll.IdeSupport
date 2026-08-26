#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// One Run-lens tag placement (issue #495): just enough to place the classic CodeLens tag on the
/// right line and know when its content has changed — never triggers a
/// <c>reqnroll/resolveTestTargets</c> call. Built purely from the symbol tree
/// (<c>RunTestCodeLensService.GetTagLocationsAsync</c>), unlike <see cref="RunTestTargetEntry"/>,
/// which is only resolved on-demand for one visible line at a time
/// (<c>RunTestCodeLensService.GetTargetsForLineAsync</c>).
/// </summary>
public sealed record RunTestLensLocation(
    /// <summary>0-based line the scenario/Outline header is on.</summary>
    int Line,
    /// <summary>
    /// Opaque, content-derived key (scenario name + Scenario/Outline kind) used only for the
    /// classic CodeLens engine's own change-detection (<c>LineElementDescription</c>) — not
    /// consumed by <see cref="RunTestCodeLensDataPointProvider.CanCreateDataPointAsync"/>, which
    /// only ever decodes the line back out of it.
    /// </summary>
    string Key);
