using System;
using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Carries the IDE identity of the connecting client so that handlers can vary behaviour per IDE.
/// Registered as a singleton in <see cref="Program.ConfigureServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity has two sources (issue #709).</b> The primary source is the <c>--ide</c> process
/// argument each IDE's glue component passes when spawning the server — it is available before the
/// client connects, which is why the log file prefix and the capability decisions in
/// <see cref="Program.ConfigureServer"/> can depend on it. The secondary source is
/// <c>InitializeParams.ClientInfo</c>, the LSP-standard field the client self-reports in the
/// <c>initialize</c> request; it arrives only once the client connects, so it is recorded by
/// <see cref="ApplyClientInfo"/> from <c>OnInitialized</c>.
/// </para>
/// <para>
/// <c>--ide</c> always wins when it is present, so no shipped client's behaviour changes — every
/// one of them passes the flag. <see cref="ApplyClientInfo"/> only fills in an identity when
/// <c>--ide</c> was absent, which makes it a <em>fallback</em> for a glue component that forgot the
/// argument, and a <em>cross-check</em> for everyone else (the recorded
/// <see cref="ClientName"/>/<see cref="ClientVersion"/> are logged at startup, so a disagreement
/// with <c>--ide</c> is visible in the log rather than silent).
/// </para>
/// </remarks>
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
        IdeArgument = ide;
        Ide = ide;
        LogLevel = logLevel;
        SupportsCodeLensResolve = supportsCodeLensResolve;
    }

    /// <summary>
    /// The effective IDE identity: the <c>--ide</c> value when the client passed one, otherwise the
    /// identifier derived from <c>InitializeParams.ClientInfo</c> by <see cref="ApplyClientInfo"/>,
    /// otherwise <see langword="null"/>. See the remarks on this class for why the flag wins.
    /// </summary>
    /// <remarks>
    /// Settable after construction — but only ever by <see cref="ApplyClientInfo"/>, and only from a
    /// <see langword="null"/>/empty value — because <c>ClientInfo</c> does not exist yet when the DI
    /// container (and this instance) is built. The write happens during the <c>initialize</c>
    /// handshake, before any request is dispatched, so readers on later threads always observe the
    /// resolved value.
    /// </remarks>
    public string? Ide { get; private set; }

    /// <summary>
    /// The raw <c>--ide</c> process argument, or <see langword="null"/> when the client did not pass
    /// one. Preserved verbatim (including an empty string) so a log line or a test can tell "the flag
    /// was absent" apart from "the flag was present but unrecognized" — unlike <see cref="Ide"/>,
    /// which may have been filled in from <c>ClientInfo</c> by then.
    /// </summary>
    public string? IdeArgument { get; }

    /// <summary>
    /// <c>InitializeParams.ClientInfo.Name</c> as self-reported by the client, or
    /// <see langword="null"/> before the client connects (or when it reported none). Recorded for
    /// diagnostics — see the remarks on this class.
    /// </summary>
    public string? ClientName { get; private set; }

    /// <summary>
    /// <c>InitializeParams.ClientInfo.Version</c> as self-reported by the client, or
    /// <see langword="null"/> when absent. Not currently branched on anywhere (issue #709
    /// deliberately does not narrow the VS semantic-tokens workaround by version); recorded so the
    /// log carries the exact client build that produced a session.
    /// </summary>
    public string? ClientVersion { get; private set; }

    /// <summary>
    /// True when <see cref="Ide"/> was derived from <c>ClientInfo</c> because no <c>--ide</c> was
    /// supplied. Always false for a client that passes the flag, which is every shipped client.
    /// </summary>
    public bool IdeResolvedFromClientInfo { get; private set; }

    /// <summary>
    /// Records the client's self-reported <c>InitializeParams.ClientInfo</c> and, when the client
    /// passed no <c>--ide</c> argument, resolves <see cref="Ide"/> from it. Called once from
    /// <c>OnInitialized</c> in <see cref="Program.ConfigureServer"/>.
    /// </summary>
    /// <param name="clientInfo">
    /// The <c>initialize</c> request's <c>ClientInfo</c>, or <see langword="null"/> when the client
    /// omitted it. An omitted or nameless <c>ClientInfo</c> leaves <see cref="Ide"/> untouched.
    /// </param>
    /// <remarks>
    /// <c>--ide</c> is never overwritten: when the flag is present this only records the
    /// self-reported identity for the startup log line, which is what makes it a cross-check rather
    /// than a second source of truth.
    /// </remarks>
    public void ApplyClientInfo(ClientInfo? clientInfo)
    {
        if (clientInfo is null) return;

        if (!string.IsNullOrWhiteSpace(clientInfo.Name))
            ClientName = clientInfo.Name;

        if (!string.IsNullOrWhiteSpace(clientInfo.Version))
            ClientVersion = clientInfo.Version;

        // The flag wins, and an empty/whitespace --ide counts as absent (the glue components pass a
        // literal, so "" only ever means "the argument was wired up with nothing in it").
        if (!string.IsNullOrWhiteSpace(Ide) || ClientName is null) return;

        var resolved = MapClientInfoNameToIde(ClientName);
        if (resolved is null) return;

        Ide = resolved;
        IdeResolvedFromClientInfo = true;
    }

    /// <summary>
    /// Maps a client's self-reported <c>ClientInfo.Name</c> onto this server's <c>--ide</c>
    /// identifier vocabulary, or <see langword="null"/> when the name matches no known client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Best-effort by design, and only ever consulted when <c>--ide</c> is absent — so a miss or a
    /// wrong guess cannot affect any shipped client. Matching is case-insensitive substring, not
    /// equality, because each client's exact string is its own to choose: VS Code's
    /// <c>vscode-languageclient</c> sends a product name such as "Visual Studio Code", while a client
    /// that has adopted this server's own vocabulary would send a bare identifier such as
    /// "visualstudio". Both are accepted, and an unrecognized name resolves to
    /// <see langword="null"/> rather than being coerced onto the nearest known IDE.
    /// </para>
    /// <para>
    /// <b>Order matters.</b> "Visual Studio Code" contains "Visual Studio", so the VS Code entries
    /// must be tested first — otherwise a VS Code client would resolve to Visual Studio and receive
    /// the VS-only semantic-token push path, which is exactly the class of silent misattribution
    /// <see cref="Logging.LspIdeSupportLogger"/>'s <c>lsp</c> fallback prefix exists to avoid.
    /// </para>
    /// </remarks>
    private static string? MapClientInfoNameToIde(string clientName)
    {
        // Order: most specific first. "visual studio code" before "visual studio" is load-bearing.
        if (Contains("visual studio code")) return "vscode";
        if (Contains("vscode")) return "vscode";
        if (Contains("visual studio")) return "visualstudio";
        if (Contains("visualstudio")) return "visualstudio";
        if (Contains("rider")) return "rider";
        return null;

        bool Contains(string token) =>
            clientName.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

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
