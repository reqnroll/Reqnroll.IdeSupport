using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Documents;

/// <summary>DocumentBufferExtensions</summary>
public static class DocumentBufferExtensions
{
    /// <summary>Wraps a <see cref="DocumentBuffer"/> as an <see cref="IGherkinTextSnapshot"/> for the Gherkin parser/formatter, defaulting the version to 0 when unset.</summary>
    public static IGherkinTextSnapshot ToGherkinTextSnapshot(this DocumentBuffer buffer)
            => new LspTextSnapshot(buffer.Uri.ToString(), buffer.Version ?? 0, buffer.Text);
}
