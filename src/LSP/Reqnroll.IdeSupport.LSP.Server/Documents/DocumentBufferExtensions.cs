using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;

namespace Reqnroll.IdeSupport.LSP.Server.Documents;

/// <summary>DocumentBufferExtensions</summary>
public static class DocumentBufferExtensions
{
    /// <summary>Wraps a <see cref="DocumentBuffer"/> as an <see cref="IGherkinTextSnapshot"/> for the Gherkin parser/formatter, defaulting the version to 0 when unset.</summary>
    public static IGherkinTextSnapshot ToGherkinTextSnapshot(this DocumentBuffer buffer)
            => new LspTextSnapshot(buffer.Uri.ToString(), buffer.Version ?? 0, buffer.Text);

    /// <summary>
    /// How long output for <paramref name="uri"/> should wait between runs while it is being edited, from the size of
    /// its buffer (see <see cref="OutputThrottlePolicy"/>). Zero when the document is not open.
    /// </summary>
    public static TimeSpan ThrottleInterval(this IDocumentBufferService buffers, DocumentUri uri)
        => buffers.TryGet(uri, out var buffer) && buffer is not null
            ? OutputThrottlePolicy.IntervalFor(buffer.Text)
            : TimeSpan.Zero;
}
