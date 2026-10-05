#nullable enable

using System;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Editor;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Documents;

/// <summary>What <see cref="FeatureBufferContentTypeGuard"/> should do with one text buffer.</summary>
internal enum FeatureBufferContentTypeAction
{
    /// <summary>Not a <c>.feature</c> file: not ours to touch.</summary>
    NotAFeatureFile,

    /// <summary>Already <c>Gherkin</c> (or a type derived from it): nothing to do.</summary>
    AlreadyGherkin,

    /// <summary>The <c>Gherkin</c> content type is not registered, so there is nothing to re-type to.</summary>
    GherkinUnavailable,

    /// <summary>
    /// Not an ordinary text buffer (e.g. <c>inert</c> or a projection buffer): some other component
    /// owns it deliberately, so leave it alone.
    /// </summary>
    NotATextBuffer,

    /// <summary>A <c>.feature</c> file in a plain text buffer of the wrong type: re-type it to <c>Gherkin</c>.</summary>
    Retype,
}

/// <summary>
/// Decides whether a <c>.feature</c> buffer needs re-typing, and describes content types for the
/// log. Split out from <see cref="FeatureBufferContentTypeGuard"/> so the rule is testable without
/// a live editor.
/// </summary>
internal static class FeatureBufferContentTypePolicy
{
    /// <summary>Decides what to do with a buffer whose document is <paramref name="filePath"/>.</summary>
    public static FeatureBufferContentTypeAction Decide(string? filePath, IContentType current, IContentType? gherkin)
    {
        if (RdtDocumentInitialization.Classify(filePath) != RdtDocumentKind.Feature)
            return FeatureBufferContentTypeAction.NotAFeatureFile;

        if (current.IsOfType(VsWellKnownIds.GherkinContentType))
            return FeatureBufferContentTypeAction.AlreadyGherkin;

        if (gherkin is null)
            return FeatureBufferContentTypeAction.GherkinUnavailable;

        if (!current.IsOfType(StandardContentTypeNames.Text))
            return FeatureBufferContentTypeAction.NotATextBuffer;

        return FeatureBufferContentTypeAction.Retype;
    }

    /// <summary>Formats a content type and its direct base types, e.g. <c>'text' (bases: any)</c>.</summary>
    public static string Describe(IContentType? contentType)
    {
        if (contentType is null)
            return "(not registered)";

        var bases = contentType.BaseTypes.Select(b => b.TypeName).ToList();
        return bases.Count == 0
            ? $"'{contentType.TypeName}' (no bases)"
            : $"'{contentType.TypeName}' (bases: {string.Join(", ", bases)})";
    }
}

/// <summary>
/// Makes sure every <c>.feature</c> text buffer has the <c>Gherkin</c> content type, and logs the
/// content type Visual Studio assigned before changing anything (issue #78).
/// </summary>
/// <remarks>
/// <para>
/// A <c>.feature</c> tab restored before the <c>Gherkin</c> content type existed keeps whatever
/// type <c>.feature</c> had at the time, for the life of that tab. Nothing keyed on
/// <c>[ContentType("Gherkin")]</c> attaches to it (classifier, CodeLens taggers, navigation bar,
/// LSP view features), and only closing and reopening it recovered. <c>GherkinContentTypeDefinition</c>
/// is the primary fix: it registers the type statically so it exists at restore time. This class
/// is the safety net for buffers created before even that definition was in effect, the same way
/// the HLSL-LSP extension re-types already-open buffers with <see cref="ITextBuffer.ChangeContentType"/>.
/// </para>
/// <para>
/// Two entry points share one rule (<see cref="FeatureBufferContentTypePolicy"/>): a sweep of the
/// documents already open when the package loads, and <see cref="ITextDocumentFactoryService.TextDocumentCreated"/>
/// for every document created afterwards — including restored stubs, whose text document only
/// exists once they initialize. The sweep never forces a stub to initialize; it reads doc data
/// only for documents that are already initialized (see <see cref="DocumentInitializationMonitor"/>
/// for why).
/// </para>
/// <para>
/// Re-typing reaches taggers and classifiers (the editor re-queries them on
/// <see cref="ITextBuffer.ContentTypeChanged"/>). It does not replay
/// <c>IVsTextViewCreationListener</c>s for views that already exist, so the navigation bar and the
/// command filters only attach to views created after the re-type. That gap is why the static
/// definition, not this class, is the primary fix.
/// </para>
/// <para>
/// Runs on the UI thread. The one exception is the <c>TextDocumentCreated</c> handler, which can
/// be raised on any thread and marshals to the UI thread itself.
/// </para>
/// </remarks>
internal sealed class FeatureBufferContentTypeGuard : IDisposable
{
    /// <summary>Identifies this component as the source of the content-type change.</summary>
    private static readonly object ContentTypeEditTag = new();

    private readonly IContentTypeRegistryService _contentTypes;
    private readonly ITextDocumentFactoryService _textDocuments;
    private readonly IVsEditorAdaptersFactoryService _editorAdapters;
    private readonly IIdeSupportLogger _logger;
    private bool _disposed;

    private FeatureBufferContentTypeGuard(
        IContentTypeRegistryService contentTypes,
        ITextDocumentFactoryService textDocuments,
        IVsEditorAdaptersFactoryService editorAdapters,
        IIdeSupportLogger logger)
    {
        _contentTypes = contentTypes;
        _textDocuments = textDocuments;
        _editorAdapters = editorAdapters;
        _logger = logger;
    }

    /// <summary>
    /// Logs the registry state, re-types already-open <c>.feature</c> buffers where needed, and
    /// subscribes to document creation. Returns <see langword="null"/> if anything it needs is
    /// unavailable — this must never break package initialization.
    /// </summary>
    public static FeatureBufferContentTypeGuard? TryStart(
        IComponentModel? componentModel,
        IVsRunningDocumentTable? rdt,
        IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var contentTypes = componentModel?.GetService<IContentTypeRegistryService>();
            var fileExtensions = componentModel?.GetService<IFileExtensionRegistryService>();
            var textDocuments = componentModel?.GetService<ITextDocumentFactoryService>();
            var editorAdapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            if (contentTypes is null || fileExtensions is null || textDocuments is null || editorAdapters is null)
            {
                logger.LogWarning("FeatureBufferContentTypeGuard: editor services unavailable; not started.");
                return null;
            }

            var guard = new FeatureBufferContentTypeGuard(contentTypes, textDocuments, editorAdapters, logger);

            logger.LogInfo(
                $"FeatureBufferContentTypeGuard: at package load, content type '{VsWellKnownIds.GherkinContentType}' is " +
                $"{FeatureBufferContentTypePolicy.Describe(contentTypes.GetContentType(VsWellKnownIds.GherkinContentType))}; " +
                $"'{GherkinContentTypeDefinition.FeatureFileExtension}' maps to " +
                $"{FeatureBufferContentTypePolicy.Describe(fileExtensions.GetContentTypeForExtension(GherkinContentTypeDefinition.FeatureFileExtension))}.");

            textDocuments.TextDocumentCreated += guard.OnTextDocumentCreated;

            // Caught separately: once subscribed, the guard must be returned (and so disposable)
            // even if the one-off sweep fails.
            try
            {
                guard.SweepOpenDocuments(rdt);
            }
            catch (Exception ex)
            {
                logger.LogException(ex, "FeatureBufferContentTypeGuard: open-document sweep failed.");
            }

            return guard;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "FeatureBufferContentTypeGuard: failed to start.");
            return null;
        }
    }

    /// <summary>Checks every initialized <c>.feature</c> document that is already open.</summary>
    private void SweepOpenDocuments(IVsRunningDocumentTable? rdt)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (rdt is not IVsRunningDocumentTable4 rdt4)
        {
            _logger.LogInfo("FeatureBufferContentTypeGuard: RDT unavailable; skipping the open-document sweep.");
            return;
        }

        rdt.GetRunningDocumentsEnum(out var docs);
        if (docs is null)
            return;

        var cookies = new uint[1];
        while (docs.Next(1, cookies, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            var cookie = cookies[0];

            // Stub-safe: neither call creates the doc data.
            var moniker = rdt4.GetDocumentMoniker(cookie);
            if (RdtDocumentInitialization.Classify(moniker) != RdtDocumentKind.Feature)
                continue;

            var flags = (_VSRDTFLAGS4)rdt4.GetDocumentFlags(cookie);
            if ((flags & _VSRDTFLAGS4.RDT_PendingInitialization) != 0)
            {
                _logger.LogInfo(
                    $"FeatureBufferContentTypeGuard: {moniker} — still a stub; it will be checked when its text document is created.");
                continue;
            }

            var buffer = TryGetTextBuffer(rdt4.GetDocumentData(cookie));
            if (buffer is null)
            {
                _logger.LogInfo($"FeatureBufferContentTypeGuard: {moniker} — no text buffer in its doc data; skipped.");
                continue;
            }

            Inspect(buffer, moniker, "already open at package load", logUnchangedAtInfo: true);
        }
    }

    private ITextBuffer? TryGetTextBuffer(object? docData)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var vsTextBuffer = docData switch
        {
            IVsTextBuffer textBuffer => textBuffer,
            IVsTextBufferProvider provider when provider.GetTextBuffer(out var lines) == VSConstants.S_OK => lines,
            _ => null,
        };

        return vsTextBuffer is null ? null : _editorAdapters.GetDocumentBuffer(vsTextBuffer);
    }

    /// <remarks>
    /// Text documents can be created off the UI thread by other components, so the path filter
    /// runs first (it is thread-safe and rejects almost every document) and the inspection is
    /// marshalled to the UI thread when needed.
    /// </remarks>
    private void OnTextDocumentCreated(object? sender, TextDocumentEventArgs e)
    {
        var document = e.TextDocument;
        if (_disposed || RdtDocumentInitialization.Classify(document.FilePath) != RdtDocumentKind.Feature)
            return;

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_disposed)
                    Inspect(document.TextBuffer, document.FilePath, "text document created", logUnchangedAtInfo: false);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "FeatureBufferContentTypeGuard: TextDocumentCreated handling failed.");
            }
        });
    }

    /// <summary>Logs the buffer's current content type and re-types it to <c>Gherkin</c> if the policy says so.</summary>
    /// <param name="logUnchangedAtInfo">
    /// Log an already-correct buffer at <c>Info</c> (the one-off package-load sweep) rather than
    /// <c>Verbose</c> (every later document open).
    /// </param>
    private void Inspect(ITextBuffer buffer, string path, string origin, bool logUnchangedAtInfo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var before = buffer.ContentType;
        var gherkin = _contentTypes.GetContentType(VsWellKnownIds.GherkinContentType);
        var action = FeatureBufferContentTypePolicy.Decide(path, before, gherkin);
        var description = FeatureBufferContentTypePolicy.Describe(before);

        switch (action)
        {
            case FeatureBufferContentTypeAction.NotAFeatureFile:
                return;

            case FeatureBufferContentTypeAction.AlreadyGherkin:
                var message = $"FeatureBufferContentTypeGuard: {path} — content type {description} ({origin}); no change needed.";
                if (logUnchangedAtInfo)
                    _logger.LogInfo(message);
                else
                    _logger.LogVerbose(message);
                return;

            case FeatureBufferContentTypeAction.GherkinUnavailable:
                _logger.LogWarning(
                    $"FeatureBufferContentTypeGuard: {path} — content type {description} ({origin}), but " +
                    $"'{VsWellKnownIds.GherkinContentType}' is not registered; cannot re-type it.");
                return;

            case FeatureBufferContentTypeAction.NotATextBuffer:
                _logger.LogInfo(
                    $"FeatureBufferContentTypeGuard: {path} — content type {description} ({origin}) is not a plain " +
                    "text buffer; leaving it alone.");
                return;

            case FeatureBufferContentTypeAction.Retype:
                try
                {
                    buffer.ChangeContentType(gherkin!, ContentTypeEditTag);
                    _logger.LogInfo(
                        $"FeatureBufferContentTypeGuard: {path} — re-typed from {description} to " +
                        $"{FeatureBufferContentTypePolicy.Describe(buffer.ContentType)} ({origin}).");
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, $"FeatureBufferContentTypeGuard: {path} — re-typing from {description} failed ({origin}).");
                }
                return;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _textDocuments.TextDocumentCreated -= OnTextDocumentCreated;
    }
}
