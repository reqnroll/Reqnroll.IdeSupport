#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

/// <summary>
/// Keeps every clickable Gherkin tag permanently styled as a link (issue #921). Visual Studio only draws its
/// Ctrl+hover underline for the tag under the pointer, which hides which tags are links at all; this classifies each
/// link's span with the editor's own <c>url</c> classification (the one it uses for URLs in plain text: link colour
/// and underline), the way the legacy extension's <c>UrlTag</c> did.
/// </summary>
/// <remarks>
/// A classifier rather than a <c>UrlTag</c> tagger on purpose: the editor's Go To Definition mouse handler stands
/// down wherever a <c>UrlTag</c> exists (decompiled from <c>Microsoft.VisualStudio.Platform.VSEditor.dll</c>,
/// <c>ExistsUrlTagAt</c>), which would bypass <see cref="TagLinkNavigableSymbolProvider"/> - the Ctrl+click that opens
/// the link, applies the http(s)-only rule and reports the telemetry event.
/// </remarks>
[Export(typeof(IClassifierProvider))]
[Name("Reqnroll TagLinkClassifierProvider")]
[ContentType(VsWellKnownIds.GherkinContentType)]
internal sealed class TagLinkClassifierProvider : IClassifierProvider
{
    /// <summary>The editor's classification type for URLs in text (<c>UrlClassifier</c> uses the same name).</summary>
    internal const string UrlClassificationTypeName = "url";

    private readonly IClassificationTypeRegistryService _registry;
    private readonly IIdeSupportLogger _logger;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public TagLinkClassifierProvider(IClassificationTypeRegistryService registry, IIdeSupportLogger logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public IClassifier GetClassifier(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() =>
            new TagLinkClassifier(TagLinkTracker.GetOrCreate(textBuffer, _logger), _registry.GetClassificationType(UrlClassificationTypeName)));
}

/// <summary>Classifies each clickable tag's span with the <c>url</c> classification type.</summary>
internal sealed class TagLinkClassifier : IClassifier
{
    private readonly TagLinkTracker _tracker;
    private readonly IClassificationType? _urlType;

    internal TagLinkClassifier(TagLinkTracker tracker, IClassificationType? urlType)
    {
        _tracker = tracker;
        _urlType = urlType;
        tracker.LinksChanged += (_, e) => ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(e.Span));
    }

    /// <inheritdoc />
    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;

    /// <inheritdoc />
    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        var result = new List<ClassificationSpan>();
        if (_urlType is null)
            return result;

        foreach (var link in _tracker.Links)
        {
            var linkSpan = link.Span.GetSpan(span.Snapshot);
            if (span.OverlapsWith(linkSpan))
                result.Add(new ClassificationSpan(linkSpan, _urlType));
        }
        return result;
    }
}
