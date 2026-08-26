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
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Classic MEF <see cref="ITaggerProvider"/> supplying <see cref="ICodeLensTag"/>s for
/// <c>Gherkin</c> buffers (Run CodeLens — design doc §5/§6, issue #262). Constructs the shared
/// <see cref="LineKeyedCodeLensTagger{TEntry}"/>, supplying only this feature's fetch/group/encode
/// functions (follow-up — this used to construct its own bespoke <c>RunTestCodeLensTagger</c>,
/// near-identical to <c>HookCodeLensTaggerProvider</c>'s).
/// </summary>
[Export(typeof(ITaggerProvider))]
[ContentType("Gherkin")]
[TagType(typeof(ICodeLensTag))]
internal sealed class RunTestCodeLensTaggerProvider : ITaggerProvider
{
    /// <inheritdoc />
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) != typeof(ICodeLensTag))
            return null;

        if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return null;

        string fileUri;
        try
        {
            fileUri = new Uri(doc.FilePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }

        return buffer.Properties.GetOrCreateSingletonProperty(
            typeof(LineKeyedCodeLensTagger<RunTestLensLocation>),
            () => new LineKeyedCodeLensTagger<RunTestLensLocation>(
                buffer, doc.FilePath, fileUri,
                FetchAsync, e => e.Line, EncodeElementDescription, RunTestCodeLensRedirect.TaggerRegistry)) as ITagger<T>;
    }

    /// <summary>
    /// Fetches tag placements only (issue #495) — the symbol tree, with no
    /// <c>reqnroll/resolveTestTargets</c> calls. The actual target(s) for a line are resolved
    /// lazily, once that line's own <see cref="RunTestCodeLensDataPoint"/> is created, not here.
    /// </summary>
    private static async Task<IReadOnlyList<RunTestLensLocation>?> FetchAsync(string fileUri, CancellationToken ct)
    {
        var fetch = RunTestCodeLensRedirect.GetTagLocationsAsync;
        if (fetch is null)
            return null;
        return await fetch(fileUri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the revision-key string for one line's tag — the trailing component is opaque to
    /// data-point providers by design (see <see cref="LineElementDescription"/>), it just has to
    /// change whenever the scenario's own identity (name/kind) changes. There is always exactly one
    /// <see cref="RunTestLensLocation"/> per line (one scenario header per line), so no ordering
    /// concern like the old resolved-target grouping had.
    /// </summary>
    internal static string EncodeElementDescription(int line, IEnumerable<RunTestLensLocation> entriesOnLine) =>
        LineElementDescription.Encode(line, entriesOnLine.Select(e => e.Key));
}
