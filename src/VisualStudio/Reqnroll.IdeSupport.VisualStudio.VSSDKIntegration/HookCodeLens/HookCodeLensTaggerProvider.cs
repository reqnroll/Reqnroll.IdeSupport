#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Classic MEF <see cref="ITaggerProvider"/> supplying <see cref="ICodeLensTag"/>s for
/// <c>Gherkin</c> buffers (hook-match-count CodeLens — issue #372, unblocking #269 for
/// Visual Studio). Content-type scoped, like <c>GherkinDropdownBarTextViewCreationListener</c> —
/// no code-element/Roslyn model is needed for this API, unlike VS.Extensibility's
/// <c>ICodeLensProvider</c>. Constructs the shared <see cref="LineKeyedCodeLensTagger{TEntry}"/>,
/// supplying only this feature's fetch/group/encode functions (issue #262 follow-up — this used to
/// construct its own bespoke <c>HookCodeLensTagger</c>, near-identical to
/// <c>RunTestCodeLensTaggerProvider</c>'s).
/// </summary>
[Export(typeof(ITaggerProvider))]
[ContentType("Gherkin")]
[TagType(typeof(ICodeLensTag))]
internal sealed class HookCodeLensTaggerProvider : ITaggerProvider
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
            typeof(LineKeyedCodeLensTagger<HookFeatureLensEntry>),
            () => new LineKeyedCodeLensTagger<HookFeatureLensEntry>(
                buffer, doc.FilePath, fileUri,
                FetchAsync, e => e.Line, EncodeElementDescription, HookCodeLensRedirect.TaggerRegistry)) as ITagger<T>;
    }

    private static async Task<IReadOnlyList<HookFeatureLensEntry>?> FetchAsync(string fileUri, CancellationToken ct)
    {
        var fetch = HookCodeLensRedirect.GetLensesAsync;
        if (fetch is null)
            return null;
        return await fetch(fileUri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the revision-key strings for one line's group of hook lenses — the trailing
    /// component is opaque to data-point providers by design (see <see cref="LineElementDescription"/>),
    /// it just has to change whenever the line's actual lens content changes.
    /// </summary>
    internal static string EncodeElementDescription(int line, IEnumerable<HookFeatureLensEntry> entriesOnLine) =>
        LineElementDescription.Encode(line, entriesOnLine
            .OrderBy(e => e.AlwaysShowPicker).ThenBy(e => e.NavLine).ThenBy(e => e.NavChar)
            .Select(e => string.Join(",",
                e.Title,
                e.NavLine.ToString(CultureInfo.InvariantCulture),
                e.NavChar.ToString(CultureInfo.InvariantCulture),
                e.OwnLevelOnly ? "1" : "0",
                e.AlwaysShowPicker ? "1" : "0")));
}
