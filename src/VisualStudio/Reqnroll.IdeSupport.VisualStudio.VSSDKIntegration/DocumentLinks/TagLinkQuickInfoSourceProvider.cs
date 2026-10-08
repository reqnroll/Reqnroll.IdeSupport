#nullable enable

using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

/// <summary>
/// Shows where a clickable Gherkin tag leads when the pointer rests on it, with no modifier key (issue #921): the
/// target URL and how to follow it, like the legacy extension's link tooltip. Reads the links from the shared
/// <see cref="TagLinkTracker"/> and answers nothing off a link, so other Quick Info on the line is unaffected.
/// </summary>
[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("Reqnroll TagLinkQuickInfoSourceProvider")]
[ContentType(VsWellKnownIds.GherkinContentType)]
internal sealed class TagLinkQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    /// <summary>The gesture hint under the URL (matches Visual Studio's own wording for URLs in text).</summary>
    internal const string FollowHint = "CTRL + click to follow link";

    private readonly IIdeSupportLogger _logger;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public TagLinkQuickInfoSourceProvider(IIdeSupportLogger logger) => _logger = logger;

    /// <inheritdoc />
    public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() =>
            new QuickInfoSource(textBuffer, TagLinkTracker.GetOrCreate(textBuffer, _logger)));

    internal sealed class QuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _buffer;
        private readonly TagLinkTracker _tracker;

        internal QuickInfoSource(ITextBuffer buffer, TagLinkTracker tracker)
        {
            _buffer = buffer;
            _tracker = tracker;
        }

        public Task<QuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            var snapshot = _buffer.CurrentSnapshot;
            var point = session.GetTriggerPoint(snapshot);
            if (point is null)
                return Task.FromResult<QuickInfoItem?>(null);

            var link = _tracker.FindLinkAt(point.Value);
            if (link is null)
                return Task.FromResult<QuickInfoItem?>(null);

            var content = new ContainerElement(
                ContainerElementStyle.Stacked,
                new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, link.Value.Target)),
                new ClassifiedTextElement(new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, FollowHint)));

            return Task.FromResult<QuickInfoItem?>(new QuickInfoItem(link.Value.Span, content));
        }

        public void Dispose()
        {
        }
    }
}
