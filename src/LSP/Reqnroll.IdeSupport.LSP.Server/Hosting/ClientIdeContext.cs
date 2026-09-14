using System;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Carries the <c>--ide</c> identifier of the connecting client so that handlers can vary
/// behaviour per IDE.  Registered as a singleton in <see cref="Program.ConfigureServer"/>.
/// </summary>
public sealed class ClientIdeContext
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  codeLens/resolve OPT-IN ALLOWLIST — deliberately EMPTY (issue #471).
    // ─────────────────────────────────────────────────────────────────────────────
    //  The server CAN defer the expensive per-lens count to codeLens/resolve
    //  (see StepCodeLensHandler.ResolveAsync / HookMatchCountCodeLensHandler.ResolveAsync
    //  and CodeLensResolveHandler), and it declares codeLensProvider.resolveProvider = true
    //  so a capable client may use it. But deferral is only safe when the CLIENT actually
    //  performs the resolve round trip, and NO client this repo ships does today:
    //
    //    * VS Code  — src/VSCode/src/commands/stepCodeLens.ts registers a hand-rolled
    //                 vscode.CodeLensProvider that does NOT implement resolveCodeLens and
    //                 discards lens.data when constructing vscode.CodeLens objects, so
    //                 codeLens/resolve is never sent and a placeholder lens never renders.
    //    * Rider    — src/Rider/.../StepUsagesCodeVisionProvider.kt filters out any lens
    //                 whose command == null before rendering, silently dropping every
    //                 deferred lens.
    //    * Visual Studio — resolve support unconfirmed; never exercised.
    //
    //  Confirmed live in VS Code during this plan's Task 9 manual verification: `.cs`
    //  step-usage lenses vanished entirely and `.feature` hook-match lenses degraded.
    //  Hence the gate is an explicit OPT-IN allowlist, not an inverted "everyone but VS"
    //  check — the inverted form is exactly the bug this replaces.
    //
    //  TO ADD A CLIENT: first make that client implement the resolve round trip
    //  (VS Code: implement CodeLensProvider.resolveCodeLens AND thread the server's
    //  `data` payload through onto the vscode.CodeLens; Rider: render command == null
    //  lenses as a placeholder CodeVision entry and issue codeLens/resolve to fill them
    //  in), verify it live against a large solution, THEN add its `--ide` identifier here.
    private static readonly HashSet<string> CodeLensResolveCapableIdes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // (intentionally empty — see the note above)
        };

    /// <summary>Initializes a new instance of the <see cref="ClientIdeContext"/> class.</summary>
    public ClientIdeContext(string? ide, TraceLevel logLevel = TraceLevel.Warning)
        : this(ide, ide is not null && CodeLensResolveCapableIdes.Contains(ide), logLevel)
    {
    }

    /// <summary>
    /// Test seam: builds a context with <see cref="SupportsCodeLensResolve"/> forced to
    /// <paramref name="supportsCodeLensResolve"/>, so the deferred-resolve branch stays covered by
    /// unit tests even while <see cref="CodeLensResolveCapableIdes"/> is empty. Never used in
    /// production code — the public constructor is the only path the server takes.
    /// </summary>
    internal ClientIdeContext(string? ide, bool supportsCodeLensResolve, TraceLevel logLevel = TraceLevel.Warning)
    {
        Ide = ide;
        LogLevel = logLevel;
        SupportsCodeLensResolve = supportsCodeLensResolve;
    }

    /// <summary>The raw <c>--ide</c> value, or <see langword="null"/> when absent.</summary>
    public string? Ide { get; }

    /// <summary>
    /// The file/protocol log verbosity requested via <c>--log-level</c>, defaulting to
    /// <see cref="TraceLevel.Warning"/> when the client did not specify one.
    /// </summary>
    public TraceLevel LogLevel { get; }

    /// <summary>
    /// True when the connecting client is Visual Studio, whose built-in LSP semantic-token
    /// colorizer cannot map custom token types — so the server pushes tokens to it instead of
    /// relying on it to pull them. See <see cref="Handlers.InternalHandlers.SemanticTokensPushHandler"/>.
    /// </summary>
    public bool IsVisualStudio => string.Equals(Ide, "visualstudio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the connecting client is VS Code, whose LSP client recognizes the built-in
    /// <c>vscode.open</c> command and executes it locally without a <c>workspace/executeCommand</c>
    /// round trip to the server. Visual Studio and Rider have no such special-casing — Visual
    /// Studio's <c>workspace.executeCommand</c> capability only ever lists its own two internal
    /// commands (<c>_ms_setClipboard</c>, <c>_ms_openUrl</c>) and forwards anything else to the
    /// server via <c>workspace/executeCommand</c>, which has no handler registered for
    /// <c>vscode.open</c> and replies "Method not found" (confirmed live, issue #563 follow-up) — so
    /// a <see cref="OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeAction"/> whose only
    /// payload is a <c>vscode.open</c> <see cref="OmniSharp.Extensions.LanguageServer.Protocol.Models.Command"/>
    /// silently does nothing when clicked there. See <see cref="Features.CodeActions.AmbiguousStepActionBuilder"/>.
    /// </summary>
    public bool IsVSCode => string.Equals(Ide, "vscode", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when the connecting client is on the <see cref="CodeLensResolveCapableIdes"/>
    /// allowlist — i.e. its LSP client is known to actually issue <c>codeLens/resolve</c> for a
    /// lens returned without a <c>Command</c>. The allowlist is empty today, so this is
    /// <see langword="false"/> for every shipped client (VS Code, Rider, Visual Studio) and all
    /// code lenses are computed eagerly, exactly as before issue #471's deferred path was added.
    /// See the extensive note on the allowlist for the evidence and the criteria for adding a client.
    /// </summary>
    public bool SupportsCodeLensResolve { get; }
}
