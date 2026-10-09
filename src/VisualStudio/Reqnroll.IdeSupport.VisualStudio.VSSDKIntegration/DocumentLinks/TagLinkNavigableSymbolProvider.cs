#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.GoToDefinition;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

/// <summary>
/// Ctrl+Click on a clickable Gherkin tag (issue #755): when the pointer is on a tag that matches a configured
/// <c>Traceability.TagLinks</c> pattern, offers a symbol that opens the tag's URL in the browser.
/// </summary>
/// <remarks>
/// <para>
/// VS's own LSP client never sends <c>textDocument/documentLink</c>, so the links are requested through
/// <see cref="TagLinkRedirect"/> (set by <c>ReqnrollLanguageClient</c>), the same bridge pattern as
/// <see cref="GoToDefinitionRedirect"/>. Only http(s) targets are opened - see <see cref="TagLinkRedirect.IsOpenableUrl"/>.
/// </para>
/// <para>
/// The editor asks each <see cref="INavigableSymbolSourceProvider"/> for a symbol in MEF order and uses the
/// <b>first non-null one</b> (decompiled from <c>Microsoft.VisualStudio.Platform.VSEditor.dll</c>,
/// <c>QueryNavigableSymbolAsync</c>). <see cref="GoToDefinitionNavigableSymbolProvider"/> offers a symbol for every
/// word in a <c>.feature</c> file, so this provider is ordered <b>before</b> it: on a tag with a link it wins, anywhere
/// else it returns <see langword="null"/> and Go To Definition behaves as before. The two providers are otherwise
/// independent - this one works whether or not <see cref="GoToDefinitionRedirect"/> is set.
/// </para>
/// </remarks>
[Export(typeof(INavigableSymbolSourceProvider))]
[Name("Reqnroll TagLinkNavigableSymbolProvider")]
[ContentType(VsWellKnownIds.GherkinContentType)]
[Order(Before = GoToDefinitionProviderName)]
public sealed class TagLinkNavigableSymbolProvider : INavigableSymbolSourceProvider
{
    /// <summary>
    /// The MEF <c>Name</c> of <see cref="GoToDefinitionNavigableSymbolProvider"/>, which this provider must precede.
    /// A unit test pins it to that class's own <c>[Name]</c> attribute.
    /// </summary>
    internal const string GoToDefinitionProviderName = "Reqnroll GoToDefinitionNavigableSymbolProvider";

    private readonly IIdeSupportLogger _logger;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public TagLinkNavigableSymbolProvider(IIdeSupportLogger logger) => _logger = logger;

    /// <summary>Creates a <see cref="NavigableSymbolSource"/> bound to the given view/buffer.</summary>
    public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer) =>
        new NavigableSymbolSource(textView, _logger);

    /// <summary>Per-view source: on each hover/click, offers a link symbol when the pointer is on a tag with a link.</summary>
    // internal rather than private so Reqnroll.IdeSupport.VisualStudio.Tests can construct it without a MEF composition.
    internal sealed class NavigableSymbolSource : INavigableSymbolSource
    {
        private readonly ITextView _textView;
        private readonly IIdeSupportLogger _logger;

        internal NavigableSymbolSource(ITextView textView, IIdeSupportLogger logger)
        {
            _textView = textView;
            _logger   = logger;
        }

        public async Task<INavigableSymbol?> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken token)
        {
            var getLinks = TagLinkRedirect.GetLinksAsync;
            if (getLinks is null)
                return null;

            var fileUri = GetTextBufferFileUri(_textView.TextBuffer);
            if (fileUri.Length == 0)
                return null;

            // Server failures degrade to "no link" (the next provider takes over), never an error.
            try
            {
                var links = await getLinks(fileUri, token).ConfigureAwait(false);
                var line  = triggerSpan.Start.GetContainingLine();
                var char0 = triggerSpan.Start.Position - line.Start.Position;
                var link  = FindLink(links, line.LineNumber, char0);
                if (link is null)
                    return null;

                // A non-http(s) target is refused by Navigate, so offering a symbol for it would put a
                // Ctrl+hover underline on the tag (winning over Go To Definition) that does nothing when
                // clicked. Keep the provider in step with the styling (TagLinkTracker) and Navigate (#1036).
                if (!TagLinkRedirect.IsOpenableUrl(link.Value.Target))
                {
                    _logger.LogVerbose(
                        $"TagLinkNavigableSymbolProvider: not offering non-http(s) tag link '{link.Value.Target}'.");
                    return null;
                }

                _logger.LogVerbose(
                    $"TagLinkNavigableSymbolProvider: offering tag link symbol uri='{fileUri}' at {line.LineNumber}:{char0} -> '{link.Value.Target}'");

                var snapshot = triggerSpan.Snapshot;
                var start = snapshot.GetLineFromLineNumber(link.Value.StartLine).Start + link.Value.StartChar;
                var end   = snapshot.GetLineFromLineNumber(link.Value.EndLine).Start   + link.Value.EndChar;
                return new TagLinkNavigableSymbol(new SnapshotSpan(start, end), link.Value.Target, _logger);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"TagLinkNavigableSymbolProvider: tag link lookup failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
        }

        // internal so tests can exercise the position lookup without a real text view.
        internal static TagLinkEntry? FindLink(IReadOnlyList<TagLinkEntry> links, int line0, int char0)
        {
            foreach (var link in links)
            {
                if (link.Contains(line0, char0))
                    return link;
            }
            return null;
        }

        // internal rather than private so tests can exercise it without a real VS text view/buffer.
        internal string GetTextBufferFileUri(ITextBuffer textBuffer)
        {
            try
            {
                if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                    return new Uri(doc.FilePath).AbsoluteUri;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"TagLinkNavigableSymbolProvider: failed to get file URI: {ex.Message}");
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The navigable symbol for a clickable tag: <see cref="Navigate"/> opens the link's URL in the default
    /// browser and reports the "TagLink command executed" telemetry event through <see cref="TagLinkRedirect.LinkOpened"/>.
    /// </summary>
    internal sealed class TagLinkNavigableSymbol : INavigableSymbol
    {
        private readonly string _target;
        private readonly IIdeSupportLogger _logger;

        internal TagLinkNavigableSymbol(SnapshotSpan symbolSpan, string target, IIdeSupportLogger logger)
        {
            SymbolSpan = symbolSpan;
            _target    = target;
            _logger    = logger;
        }

        public SnapshotSpan SymbolSpan { get; }

        public IEnumerable<INavigableRelationship> Relationships { get; } =
            new[] { PredefinedNavigableRelationships.Definition };

        public void Navigate(INavigableRelationship relationship)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!TagLinkRedirect.IsOpenableUrl(_target))
            {
                _logger.LogWarning($"TagLinkNavigableSymbolProvider: refusing to open non-http(s) tag link '{_target}'.");
                return;
            }

            try
            {
                VsShellUtilities.OpenSystemBrowser(_target);
                _logger.LogVerbose($"TagLinkNavigableSymbolProvider: opened tag link '{_target}'.");
                TagLinkRedirect.LinkOpened?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "TagLinkNavigableSymbolProvider: opening tag link failed");
            }
        }
    }
}
