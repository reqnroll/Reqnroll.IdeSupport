namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Behaviours and limitations that we have found, through our own development and debugging, to
/// differ between IDE clients. These are deliberately separate from the LSP-spec client
/// capabilities (<c>InitializeParams.Capabilities</c>): a facet records something the spec cannot
/// tell us (a client quirk or workaround), named for the <em>behaviour</em> rather than the IDE.
/// </summary>
/// <remarks>
/// Populated in one place, <see cref="ClientFacetResolver"/>, from the client identity (and
/// version), and exposed as <see cref="ClientIdeContext.Facets"/>. Handlers branch on a facet, never
/// on an IDE name, so a change in a client's behaviour is a one-line edit to the resolver.
/// The default (<see cref="None"/>) is the standard-LSP baseline: an unrecognized client opts in to
/// no workaround.
/// </remarks>
public sealed record ClientFacets
{
    /// <summary>The standard-LSP baseline: no client-specific workaround applies.</summary>
    public static ClientFacets None { get; } = new();

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
    /// The client's native rename applies whatever <c>WorkspaceEdit</c> the rename response carries,
    /// so the server pushes the real edit with <c>workspace/applyEdit</c> after the response and
    /// returns an empty edit in the response itself (returning the real one would apply it twice).
    /// </summary>
    public bool AppliesRenameViaPush { get; init; }

    /// <summary>
    /// After the Define Steps command the server reveals the generated file with
    /// <c>window/showDocument</c> (when the client also advertises that capability). Only set for
    /// clients that also recognise the built-in <c>vscode.open</c> command; the others forward
    /// commands to the server, where an unregistered one fails with "Method not found".
    /// </summary>
    public bool RevealsFileViaShowDocument { get; init; }

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
    /// evidence note in <see cref="ClientFacetResolver"/>.
    /// </summary>
    public bool SupportsCodeLensResolve { get; init; }
}
