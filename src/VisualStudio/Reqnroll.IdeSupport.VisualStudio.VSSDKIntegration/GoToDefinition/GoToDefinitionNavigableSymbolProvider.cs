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
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.GoToDefinition;

/// <summary>
/// Intercepts Ctrl+Click / Ctrl+hover ("Go To Definition" via the navigable-symbol API) for
/// <c>Gherkin</c> text views and redirects it to the Reqnroll LSP server via
/// <see cref="GoToDefinitionRedirect"/> — the same bridge <see cref="GoToDefinitionCommandFilter"/>
/// uses for F12 (issue #757).
/// </summary>
/// <remarks>
/// <para>
/// F12 goes through the shell's <c>IOleCommandTarget</c> chain, which <see cref="GoToDefinitionCommandFilter"/>
/// intercepts. Ctrl+Click is a completely different path: VS's own LSP client exports an
/// <c>INavigableSymbolSourceProvider</c> (decompiled as <c>NavigableSymbolSourceProvider</c> in
/// <c>Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll</c> —
/// <c>[ContentType("languageserver-base")] [Name("LSP NavigableSymbolSourceProvider")]
/// [Order(After = "Lowest Priority")]</c>). <c>Gherkin</c>'s document type derives from
/// <c>languageserver-base</c> (see <c>GherkinDocumentType</c>), so that provider applies to
/// <c>.feature</c> files too, and its <c>NavigableSymbol.Navigate</c> calls
/// <c>RemoteReferencesBroker.HandleDefinitionAsync</c> with the caret-word title — the same
/// <c>'{word}' declarations</c> window issue #757 fixed for F12 (issue #761).
/// </para>
/// <para>
/// This provider is exported directly for <c>Gherkin</c> (more specific than VS's
/// <c>languageserver-base</c>) and ordered before <see cref="VsWellKnownIds.LspNavigableSymbolSourceProviderName"/>
/// so the editor's navigable-symbol aggregation prefers it. When <see cref="GoToDefinitionRedirect.GoToDefinitionAsync"/>
/// is unset (server not yet initialized) <see cref="NavigableSymbolSource.GetNavigableSymbolAsync"/> returns
/// <see langword="null"/>, leaving the underline/click affordance entirely to VS's own (lower-priority)
/// provider — the same fallback <see cref="GoToDefinitionCommandFilter"/> uses for F12.
/// </para>
/// </remarks>
[Export(typeof(INavigableSymbolSourceProvider))]
[Name("Reqnroll GoToDefinitionNavigableSymbolProvider")]
[ContentType(VsWellKnownIds.GherkinContentType)]
[Order(Before = VsWellKnownIds.LspNavigableSymbolSourceProviderName)]
public sealed class GoToDefinitionNavigableSymbolProvider : INavigableSymbolSourceProvider
{
    private readonly ITextStructureNavigatorSelectorService _textStructureNavigatorSelectorService;
    private readonly IIdeSupportLogger _logger;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public GoToDefinitionNavigableSymbolProvider(
        ITextStructureNavigatorSelectorService textStructureNavigatorSelectorService, IIdeSupportLogger logger)
    {
        _textStructureNavigatorSelectorService = textStructureNavigatorSelectorService;
        _logger = logger;
    }

    /// <summary>Creates a <see cref="NavigableSymbolSource"/> bound to the given view/buffer.</summary>
    public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer) =>
        new NavigableSymbolSource(textView, _textStructureNavigatorSelectorService, _logger);

    /// <summary>
    /// Per-view navigable-symbol source: on each hover/click, resolves the word under the caret and,
    /// if the LSP redirect is available, hands back a symbol whose <c>Navigate</c> runs Go To
    /// Definition through it instead of VS's own handling.
    /// </summary>
    // internal rather than private so Reqnroll.IdeSupport.VisualStudio.Tests (an InternalsVisibleTo
    // friend assembly) can construct this directly to unit test it without a MEF composition.
    internal sealed class NavigableSymbolSource : INavigableSymbolSource
    {
        private readonly ITextView _textView;
        private readonly ITextStructureNavigatorSelectorService _textStructureNavigatorSelectorService;
        private readonly IIdeSupportLogger _logger;

        internal NavigableSymbolSource(
            ITextView textView, ITextStructureNavigatorSelectorService textStructureNavigatorSelectorService, IIdeSupportLogger logger)
        {
            _textView = textView;
            _textStructureNavigatorSelectorService = textStructureNavigatorSelectorService;
            _logger = logger;
        }

        public Task<INavigableSymbol?> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken token)
        {
            var redirect = GoToDefinitionRedirect.GoToDefinitionAsync;
            if (redirect is null)
            {
                _logger.LogVerbose(
                    "GoToDefinitionNavigableSymbolProvider: LSP server not initialized (no redirect set), falling through to VS's own Ctrl+Click handling.");
                return Task.FromResult<INavigableSymbol?>(null);
            }

            var fileUri = GetTextBufferFileUri(_textView.TextBuffer);
            if (fileUri.Length == 0)
                return Task.FromResult<INavigableSymbol?>(null);

            var navigator = _textStructureNavigatorSelectorService.GetTextStructureNavigator(triggerSpan.Snapshot.TextBuffer);
            var wordSpan = navigator.GetExtentOfWord(triggerSpan.Start).Span;

            var line     = wordSpan.Start.GetContainingLine();
            var line0    = line.LineNumber;
            var char0    = wordSpan.Start.Position - line.Start.Position;
            var lineText = line.GetText();

            _logger.LogVerbose(
                $"GoToDefinitionNavigableSymbolProvider: offering navigable symbol uri='{fileUri}' at {line0}:{char0}");

            return Task.FromResult<INavigableSymbol?>(new NavigableSymbol(wordSpan, fileUri, line0, char0, lineText, redirect, _logger));
        }

        public void Dispose()
        {
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
                _logger.LogWarning($"GoToDefinitionNavigableSymbolProvider: failed to get file URI: {ex.Message}");
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The navigable symbol offered for the word under the caret: its span is the same word extent VS's
    /// own provider would underline, and <see cref="Navigate"/> runs Go To Definition through the
    /// Reqnroll redirect instead of VS's <c>RemoteReferencesBroker</c>.
    /// </summary>
    internal sealed class NavigableSymbol : INavigableSymbol
    {
        private readonly string _fileUri;
        private readonly int _line0;
        private readonly int _char0;
        private readonly string _lineText;
        private readonly Func<string, int, int, string, CancellationToken, Task> _redirect;
        private readonly IIdeSupportLogger _logger;

        internal NavigableSymbol(
            SnapshotSpan symbolSpan, string fileUri, int line0, int char0, string lineText,
            Func<string, int, int, string, CancellationToken, Task> redirect, IIdeSupportLogger logger)
        {
            SymbolSpan = symbolSpan;
            _fileUri   = fileUri;
            _line0     = line0;
            _char0     = char0;
            _lineText  = lineText;
            _redirect  = redirect;
            _logger    = logger;
        }

        public SnapshotSpan SymbolSpan { get; }

        public IEnumerable<INavigableRelationship> Relationships { get; } =
            new[] { PredefinedNavigableRelationships.Definition };

        /// <summary>
        /// Runs Go To Definition through <see cref="GoToDefinitionRedirect"/>, fired and forgotten the
        /// same way <see cref="GoToDefinitionCommandFilter"/> does for F12 — VS calls this synchronously
        /// from the UI thread and does not await it.
        /// </summary>
        public void Navigate(INavigableRelationship relationship)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

#pragma warning disable VSSDK007 // fire-and-forget navigation, same reasoning as GoToDefinitionCommandFilter.Exec
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
#pragma warning restore VSSDK007
            {
                await TaskScheduler.Default;
                await _redirect(_fileUri, _line0, _char0, _lineText, VsShellUtilities.ShutdownToken).ConfigureAwait(false);
            }).FileAndForget(
                "vs/Reqnroll/GoToDefinitionNavigableSymbolProvider/Navigate",
                "Go To Definition (Ctrl+Click) failed in a .feature file",
                ex =>
                {
                    _logger.LogException(ex, "GoToDefinitionNavigableSymbolProvider: Go To Definition (Ctrl+Click) failed");
                    return true;
                });
        }
    }
}
