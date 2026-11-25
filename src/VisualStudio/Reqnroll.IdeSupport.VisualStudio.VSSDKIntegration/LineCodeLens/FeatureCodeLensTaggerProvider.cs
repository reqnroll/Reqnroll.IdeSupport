#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Classic MEF <see cref="ITaggerProvider"/> supplying the one <see cref="ICodeLensTag"/> per
/// <c>.feature</c> line that every Reqnroll classic CodeLens (Run, own-level hooks, step hooks)
/// renders on. Constructs the shared <see cref="LineKeyedCodeLensTagger{TEntry}"/> over the merged
/// Run and hook lens data.
/// </summary>
/// <remarks>
/// <para>
/// One tagger for all lens kinds, not one per feature: when two taggers each put a tag on the same
/// line, the classic CodeLens host keeps only one of them and offers that single descriptor to every
/// data-point provider (seen in the CodeLens host log: the Run provider was never offered a hook
/// tagger's descriptor, though the hook tagger had tags on the same lines). A per-feature tag
/// therefore can't tell a provider whether the line carries its kind, so the merged tag lists every
/// kind present (see <see cref="LineElementDescription"/>) and each provider accepts only its own.
/// </para>
/// <para>
/// Content-type scoped, like <c>GherkinDropdownBarTextViewCreationListener</c> — no
/// code-element/Roslyn model is needed for this API, unlike VS.Extensibility's
/// <c>ICodeLensProvider</c>.
/// </para>
/// </remarks>
[Export(typeof(ITaggerProvider))]
[ContentType(VsWellKnownIds.GherkinContentType)]
[TagType(typeof(ICodeLensTag))]
internal sealed class FeatureCodeLensTaggerProvider : ITaggerProvider
{
    /// <summary>
    /// The registry every <c>.feature</c> CodeLens tagger registers itself with; both
    /// <see cref="HookCodeLensRedirect"/> and <see cref="RunTestCodeLensRedirect"/> invalidate
    /// through it, since either kind changing changes the line's tag.
    /// </summary>
    internal static readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<FeatureLineLensEntry>> TaggerRegistry =
        new(tagger => tagger.RequestRefresh());

    /// <inheritdoc />
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) != typeof(ICodeLensTag))
            return null;

        return LineKeyedCodeLensTagger<FeatureLineLensEntry>.GetOrCreateForBuffer(
            buffer, FetchAsync, e => e.Line, EncodeElementDescription, TaggerRegistry) as ITagger<T>;
    }

    /// <summary>
    /// Fetches the Run tag placements and the hook lenses for the file and merges them. Returns
    /// <see langword="null"/> only when neither source is wired up yet; a source that isn't wired
    /// contributes no lenses until the next refresh.
    /// </summary>
    internal static async Task<IReadOnlyList<FeatureLineLensEntry>?> FetchAsync(string fileUri, CancellationToken ct)
    {
        var fetchRun = RunTestCodeLensRedirect.GetTagLocationsAsync;
        var fetchHooks = HookCodeLensRedirect.GetLensesAsync;
        if (fetchRun is null && fetchHooks is null)
            return null;

        var runTask = fetchRun?.Invoke(fileUri, ct)
            ?? Task.FromResult<IReadOnlyList<RunTestLensLocation>>(Array.Empty<RunTestLensLocation>());
        var hooksTask = fetchHooks?.Invoke(fileUri, ct)
            ?? Task.FromResult<IReadOnlyList<HookFeatureLensEntry>>(Array.Empty<HookFeatureLensEntry>());

        // WhenAll rather than two awaits in a row: if the first fetch throws, a fault in the second
        // would otherwise never be observed.
        await Task.WhenAll(runTask, hooksTask).ConfigureAwait(false);
        var runLocations = await runTask.ConfigureAwait(false);
        var hookLenses = await hooksTask.ConfigureAwait(false);

        return runLocations.Select(RunLensTagEntries.ToTagEntry)
            .Concat(hookLenses.Select(HookLensTagEntries.ToTagEntry))
            .ToList();
    }

    /// <summary>
    /// Builds the descriptor for one line: the lens kinds present, so each provider creates a data
    /// point only for its own kind, plus every lens's revision key, so the descriptor changes
    /// whenever any lens on the line does. Keys are sorted, so the order the sources return lenses
    /// in never churns the tag.
    /// </summary>
    internal static string EncodeElementDescription(int line, IEnumerable<FeatureLineLensEntry> entriesOnLine)
    {
        var entries = entriesOnLine.ToList();
        return LineElementDescription.Encode(
            line,
            entries.Select(e => e.Kind),
            entries.Select(e => $"{e.Kind}:{e.RevisionKey}").OrderBy(k => k, StringComparer.Ordinal));
    }
}
