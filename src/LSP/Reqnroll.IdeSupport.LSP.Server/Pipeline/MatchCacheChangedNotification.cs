using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published after a feature document has been (re)parsed and its binding matches recomputed
/// and stored in <see cref="Core.Matching.IBindingMatchService"/> — i.e. whenever the match
/// cache for <see cref="Uri"/> changes, whether triggered by a text edit, a binding-registry
/// replacement, or a configuration change.
/// </summary>
/// <remarks>
/// This is the <c>MatchCacheChangedNotification</c> of section 3 / section 6 of the LSP IDE
/// Support design. Consumers re-read the current tags / match set rather than receiving them
/// <see cref="Pipeline.SemanticTokensRefreshHandler"/>
/// asks the client to refresh semantic tokens, and the (future) diagnostics aggregator pushes
/// <c>textDocument/publishDiagnostics</c>.
/// <para>
/// <b>Consumers must be idempotent (issue #578):</b> handling the same notification twice must
/// leave the same observable end state as handling it once — every current handler re-reads
/// current state and pushes a full result rather than an incremental delta, so this holds today,
/// but it is an invariant a new handler must preserve, not merely an accident of how the
/// existing ones happen to be written.
/// </para>
/// </remarks>
public record MatchCacheChangedNotification(
    DocumentUri Uri,
    int Version) : INotification;
