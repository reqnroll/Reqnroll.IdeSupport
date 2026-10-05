namespace Reqnroll.IdeSupport.LSP.Core.Ide;

/// <summary>
/// Behaviours and limitations that we have found, through our own development and debugging, to
/// differ between IDE clients. These are deliberately separate from the LSP-spec client
/// capabilities (<c>InitializeParams.Capabilities</c>): a behaviour records something the spec cannot
/// tell us (a client quirk or workaround), named for the <em>behaviour</em> rather than the IDE.
/// </summary>
/// <remarks>
/// Populated in one place, <see cref="IdeBehavioursResolver"/>, from the client identity (and
/// version), and exposed as <see cref="ClientIdeContext.Behaviours"/>. Handlers branch on a behaviour, never
/// on an IDE name, so a change in a client's behaviour is a one-line edit to the resolver.
/// The default (<see cref="None"/>) is the standard-LSP baseline: an unrecognized client opts in to
/// no workaround.
/// </remarks>
public sealed record IdeBehaviours
{
    /// <summary>The standard-LSP baseline: no client-specific workaround applies.</summary>
    public static IdeBehaviours None { get; } = new();

    /// <summary>
    /// The client cannot pull semantic tokens usefully (its built-in colorizer cannot map our custom
    /// token types), so the server pushes them via <c>reqnroll/semanticTokens</c> and withholds
    /// <c>full</c>/<c>range</c> from the advertised <c>semanticTokensProvider</c>. The legend is
    /// still advertised: the push path decodes against it.
    /// </summary>
    public bool RequiresPushedSemanticTokens { get; init; }

    /// <summary>
    /// The client is refreshed with the custom <c>reqnroll/refreshCodeLens</c> notification instead
    /// of the standard <c>workspace/codeLens/refresh</c> request.
    /// </summary>
    public bool UsesCustomCodeLensRefresh { get; init; }

    /// <summary>
    /// An empty <c>CompletionList</c> for a trigger-character request is treated by the client as
    /// "reject and revert the typed character" (VS 2022 would delete the <c>|</c>), so a suppressed
    /// table-row completion must return a no-op item instead.
    /// </summary>
    public bool RejectsEmptyTriggerCompletion { get; init; }

    /// <summary>
    /// The client requests completion after a deletion (Backspace/Delete, including joining lines),
    /// reporting the <em>deleted</em> text in <c>triggerCharacter</c> with <c>triggerKind</c>
    /// <c>Invoked</c> (a genuine trigger character arrives as <c>TriggerCharacter</c>). Nothing was
    /// typed, so the server answers such requests with an empty list.
    /// </summary>
    public bool RequestsCompletionAfterDeletion { get; init; }

    /// <summary>
    /// The client's native rename applies whatever <c>WorkspaceEdit</c> the rename response carries,
    /// so the server pushes the real edit with <c>workspace/applyEdit</c> after the response and
    /// returns an empty edit in the response itself (returning the real one would apply it twice).
    /// </summary>
    public bool AppliesRenameResponseEditNatively { get; init; }

    /// <summary>
    /// The client reliably opens a document in response to a <c>window/showDocument</c> request, so
    /// the server uses it to reveal the generated file after the Define Steps command (when the
    /// client also advertises that capability). Opt-in: we have only confirmed this for clients that
    /// also run the built-in <c>vscode.open</c> command locally.
    /// </summary>
    public bool HonorsShowDocumentRequests { get; init; }

    /// <summary>
    /// The client recognises the built-in <c>vscode.open</c> command and runs it locally, without a
    /// <c>workspace/executeCommand</c> round trip. Clients without this forward any command to the
    /// server, where <c>vscode.open</c> has no handler and replies "Method not found", so a code
    /// action carrying only that command would silently do nothing there.
    /// </summary>
    public bool RunsVscodeOpenCommandLocally { get; init; }

    /// <summary>
    /// The client actually issues <c>codeLens/resolve</c> for a lens returned without a
    /// <c>Command</c>, so the expensive per-lens count can be deferred to it. Opt-in: see the
    /// evidence note in <see cref="IdeBehavioursResolver"/>.
    /// </summary>
    public bool SupportsCodeLensResolve { get; init; }
}
