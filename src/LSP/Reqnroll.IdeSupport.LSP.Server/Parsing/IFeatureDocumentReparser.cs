using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Parsing;

/// <summary>
/// Formalizes the "re-parse an open <c>.feature</c> document, then publish
/// <see cref="MatchCacheChangedNotification"/>" pair (issue #578) that four independent call
/// sites previously each implemented as their own private <c>ParseAndNotifyAsync</c> copy:
/// <see cref="Features.TextSync.TextDocumentSyncHandler"/>, <see cref="BindingRegistryChangedHandler"/>,
/// <see cref="ReqnrollConfigChangedHandler"/>, and <see cref="Features.DocumentActivated.DocumentActivatedHandler"/>.
/// </summary>
/// <remarks>
/// Two distinct methods, not one with an optional parameter, because the four call sites split
/// into two genuinely different contracts rather than four copies of one:
/// <list type="bullet">
///   <item>
///   The first three already know the document is open and at what version, because each
///   obtained <paramref name="uri"/> by enumerating <see cref="Documents.IDocumentBufferService.All"/>
///   in the first place. For them, <see cref="ReparseOpenDocumentAsync"/> unconditionally
///   publishes with the version they hand in.
///   </item>
///   <item>
///   <c>DocumentActivatedHandler</c> is reached from a client-side activation signal that can
///   race ahead of <c>textDocument/didOpen</c> (issue #85), so it does not know in advance
///   whether the document is open at all, let alone at what version.
///   <see cref="ReparseIfOpenAsync"/> re-checks the buffer <em>after</em> parsing and publishes
///   only if one still exists, using its actual current version — and is a safe no-op otherwise.
///   </item>
/// </list>
/// Collapsing these into one parameterized method would hide that distinction behind a
/// nullable-version convention, which is exactly the kind of implicit contract issue #578 set
/// out to make explicit instead of merely deduplicating four copies of the same two lines.
/// </remarks>
public interface IFeatureDocumentReparser
{
    /// <summary>
    /// Re-parses <paramref name="uri"/> at <paramref name="version"/> and unconditionally
    /// publishes <see cref="MatchCacheChangedNotification"/>. Callers must already know the
    /// document is open — this does not check.
    /// </summary>
    Task ReparseOpenDocumentAsync(DocumentUri uri, int? version, CancellationToken cancellationToken);

    /// <summary>
    /// Re-parses <paramref name="uri"/> and publishes <see cref="MatchCacheChangedNotification"/>
    /// only if the document still has an open buffer afterward, using that buffer's actual
    /// current version. A safe no-op — parse still runs, nothing is published — when there is no
    /// buffer for <paramref name="uri"/> at all.
    /// </summary>
    Task ReparseIfOpenAsync(DocumentUri uri, CancellationToken cancellationToken);
}
