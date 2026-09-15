#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FormatDocument;

/// <summary>
/// Container-registered singleton holder for the runtime-created Format Document/Selection service.
/// </summary>
/// <remarks>
/// <see cref="FormatDocumentService"/> depends on <c>LspInterceptingPipe</c>, which only exists after
/// the language server connection is established — too late for plain DI construction.
/// <see cref="ReqnrollLanguageClient"/> populates this on server init and clears it on dispose;
/// <see cref="FormatDocumentRedirect"/> is set from the same service instance so the VSSDK command
/// filter can reach it.
/// </remarks>
internal sealed class FormatDocumentState
{
    /// <summary>Set once the server has initialised; null before that and after dispose.</summary>
    public FormatDocumentService? Service { get; set; }
}
