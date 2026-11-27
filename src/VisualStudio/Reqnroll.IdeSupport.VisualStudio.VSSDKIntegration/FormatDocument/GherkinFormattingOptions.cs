#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.FormatDocument;

/// <summary>
/// The editor's indentation settings sent as the LSP <c>FormattingOptions</c> of a
/// <c>textDocument/formatting</c>/<c>rangeFormatting</c> request.
/// </summary>
/// <param name="TabSize">Size of one indent level in spaces.</param>
/// <param name="InsertSpaces">True to indent with spaces, false to indent with tabs.</param>
public readonly record struct GherkinFormattingOptions(int TabSize, bool InsertSpaces)
{
    /// <summary>Used when the editor reports no usable tab size.</summary>
    public static GherkinFormattingOptions Default { get; } = new(4, true);
}
