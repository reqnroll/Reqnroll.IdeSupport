using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published after the match sets of a <em>closed</em> feature file were rewritten from disk, for
/// example when a document is closed and its unsaved editor text is dropped. The usage counts shown
/// over the step definitions the file uses may have changed.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="MatchCacheChangedNotification"/>: that one drives work for an open
/// document (diagnostics, semantic tokens, inlay hints), none of which applies to a closed file.
/// Only <see cref="CodeLensRefreshHandler"/> consumes this one. Like its sibling it is idempotent to
/// handle: the refresh it leads to re-reads current state.
/// </remarks>
public record ClosedFileMatchCacheChangedNotification(DocumentUri Uri) : INotification;
