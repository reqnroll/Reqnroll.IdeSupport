#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// A request to the language server was rejected with LSP <c>ContentModified</c> even after a
/// retry: the server's state changed while it was being answered, so the caller should ask again
/// later rather than treat the request as having produced an empty answer.
/// </summary>
/// <remarks>
/// The server (OmniSharp) cancels every in-flight Parallel request with <c>ContentModified</c>
/// whenever it processes a Serial notification — for example the extension's own
/// <c>reqnroll/projectLoaded</c>/<c>reqnroll/projectFiles</c> at startup. Mapping that to "no
/// result" let the Run CodeLens briefly cache "no scenario on this line" for a real scenario
/// (issue #800 follow-up, the same failure mode as the swallowed cancellation fixed in #778).
/// Lives here, not in the Extension project, so the VSSDK-side navigation bar can recognize it.
/// </remarks>
public sealed class LspContentModifiedException : Exception
{
    /// <summary>The LSP <c>ContentModified</c> error code.</summary>
    public const int ErrorCode = -32801;

    /// <summary>Creates the exception for <paramref name="method"/> on <paramref name="documentUri"/>.</summary>
    public LspContentModifiedException(string method, string documentUri)
        : base($"{method} for {documentUri} was rejected with ContentModified; the document changed while it was being answered.")
    {
        Method = method;
        DocumentUri = documentUri;
    }

    /// <summary>The LSP method that was rejected.</summary>
    public string Method { get; }

    /// <summary>The document the request was for.</summary>
    public string DocumentUri { get; }
}
