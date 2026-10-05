using System;
using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Core.Ide;

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
    private readonly Func<string?, string?, IdeBehaviours> _behaviourResolver;

    /// <summary>Initializes a new instance of the <see cref="ClientIdeContext"/> class.</summary>
    public ClientIdeContext(string? ide, TraceLevel logLevel = TraceLevel.Warning)
        : this(ide, IdeBehavioursResolver.Resolve, logLevel)
    {
    }

    /// <summary>
    /// Test seam: builds a context whose <see cref="Behaviours"/> are forced to
    /// <paramref name="behaviours"/> regardless of identity, so a handler branch can be exercised
    /// without faking an IDE name (and a behaviour no shipped client has, such as
    /// <see cref="IdeBehaviours.SupportsCodeLensResolve"/>, stays covered). Never used in production
    /// code — the public constructor is the only path the server takes.
    /// </summary>
    internal ClientIdeContext(string? ide, IdeBehaviours behaviours, TraceLevel logLevel = TraceLevel.Warning)
        : this(ide, (_, _) => behaviours, logLevel)
    {
    }

    private ClientIdeContext(
        string? ide, Func<string?, string?, IdeBehaviours> behaviourResolver, TraceLevel logLevel)
    {
        IdeArgument = ide;
        Ide = ide;
        LogLevel = logLevel;
        _behaviourResolver = behaviourResolver;
        Behaviours = behaviourResolver(ide, null);
    }

    /// <summary>
    /// The client-specific behaviours that apply to the connected client — the only thing handlers
    /// should branch on. Resolved by <see cref="IdeBehavioursResolver"/> from the identity and version;
    /// recomputed by <see cref="ApplyClientInfo"/> once <c>ClientInfo</c> arrives, because that can
    /// supply the identity (when <c>--ide</c> was absent) and the version.
    /// </summary>
    /// <remarks>
    /// Replaced as a whole immutable record, only during the <c>initialize</c> handshake and before
    /// any request is dispatched, so readers on later threads always observe the final value.
    /// </remarks>
    public IdeBehaviours Behaviours { get; private set; }

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
    /// <see langword="null"/> when absent. Passed to <see cref="IdeBehavioursResolver"/> so a behaviour can be
    /// narrowed by version, though no rule does today (issue #709 deliberately does not narrow the VS
    /// semantic-tokens workaround by version); also logged so the log carries the exact client build.
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
        if (string.IsNullOrWhiteSpace(Ide) && ClientName is not null
            && MapClientInfoNameToIde(ClientName) is { } resolved)
        {
            Ide = resolved;
            IdeResolvedFromClientInfo = true;
        }

        Behaviours = _behaviourResolver(Ide, ClientVersion);
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

}
