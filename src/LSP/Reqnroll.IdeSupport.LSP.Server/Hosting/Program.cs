using System.Reflection;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Tracing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <remarks>
/// <para>
/// <b>Logging split (issue #84):</b> app-level code in this server (handlers, discovery,
/// workspace/document services, etc.) logs exclusively through the DI-registered
/// <see cref="IIdeSupportLogger"/> singleton (<see cref="Logging.LspIdeSupportLogger"/>), consumed via
/// the <c>LogInfo</c>/<c>LogWarning</c>/<c>LogError</c>/... extension methods in
/// <see cref="IdeSupportLoggerExtensions"/> — this is deliberate and should stay the pattern for new
/// app-level code, rather than switching individual classes to <c>ILogger&lt;T&gt;</c>.
/// </para>
/// <para>
/// Separately, <c>ILogger&lt;T&gt;</c> (<see cref="Microsoft.Extensions.Logging"/>) is what
/// OmniSharp's own internals log through — request dispatch, DryIoc, JSON-RPC plumbing. That
/// pipeline is established by <see cref="ConfigureServer"/>'s <c>options.ConfigureLogging(...)</c>
/// call (<c>SetMinimumLevel</c>, <c>AddLanguageProtocolLogging</c>,
/// <see cref="ProtocolLoggerProvider"/>) directly into <c>options.Services</c>, gated by its own
/// <c>--protocol-log-level</c> and writing to a dedicated <c>reqnroll-*-protocol-*.log</c> file via
/// the shared <see cref="IdeSupportLoggerAdapter"/> — deliberately a separate store from the app-level
/// <see cref="IIdeSupportLogger"/> "server" log. <b>Do not re-register <c>ILoggerFactory</c>/<c>ILogger&lt;&gt;</c>
/// anywhere else in this DI container</b> (e.g. in
/// <see cref="ServiceCollectionExtensions.AddReqnrollLspCoreServices"/>) — a later registration wins
/// the last-registration-wins resolution and silently replaces this one, which previously caused
/// every OmniSharp-internal message to leak into the app-level "server" log at whatever
/// <c>--log-level</c> happened to be, instead of its own file gated by <c>--protocol-log-level</c>.
/// </para>
/// <para>
/// The three log-level dials below (<c>--log-level</c>, <c>--protocol-log-level</c>, <c>--trace</c>)
/// remain intentionally independent — see each parameter's remarks on <see cref="ConfigureServer"/>.
/// </para>
/// </remarks>
public class Program
{
    /// <summary>Entry point: parses CLI/IDE-supplied arguments, configures logging and DI services, and starts the LSP server over stdio.</summary>
    public static async Task Main(string[] args)
    {
        // Each IDE's glue component passes --ide <identifier> when spawning the server.
        // The semantic token legend no longer varies by IDE, but the identifier is retained for
        // features that may need to vary their behaviour per IDE (e.g. future static-vs-dynamic
        // capability registration decisions).
        var ideId = ParseArg(args, "--ide");

        // Each IDE's glue component may pass --log-level <level> (Off/Error/Warning/Info/Verbose)
        // when spawning the server. Defaults to Warning when absent so a normal session doesn't
        // write maximum-verbosity logs indefinitely; pass --log-level Verbose for full tracing.
        // Controls ONLY our own app-level IIdeSupportLogger file (reqnroll-*-server-*.log).
        var logLevel = ParseLogLevel(args);

        // --protocol-log-level <level> is the equivalent dial for OmniSharp's own internal
        // diagnostics (request dispatch, DryIoc, JSON-RPC plumbing — whatever the library logs via
        // ILogger<T>), deliberately decoupled from --log-level: turning up our own app logging
        // shouldn't also flood the client's Output panel (window/logMessage) or a separate protocol
        // log file with library internals, and vice versa. See ConfigureServer for where it's used.
        var protocolLogLevel = ParseProtocolLogLevel(args);

        // F41: --trace <Off/Messages/Verbose> seeds the LSP protocol trace level ($/logTrace) —
        // yet another independent dial from the two above — before the client ever connects, so an
        // IDE glue component that doesn't populate InitializeParams.Trace still gets a configurable
        // default. See ConfigureServer's initialTrace parameter for the full precedence order.
        var initialTrace = ParseTraceLevel(args);

        // Write any unhandled startup exception to a file next to the LSP inspector logs
        // so crashes are self-diagnosing without needing to capture stderr.
        try
        {
            // LanguageServer.PreInit (unlike .From) builds the DI container and constructs the
            // server WITHOUT blocking on the client's "initialize" handshake — .Services is usable
            // immediately. .From's await blocks inside Initialize() until a real client "initialize"
            // arrives, which would gate ProjectPreloadListener behind the exact thing it exists to
            // route around. See ProjectPreloadListener's remarks for the full rationale.
            var server = LanguageServer.PreInit(options =>
            {
                // Production transport: the IDE talks to the server over stdio.
                options.WithInput(Console.OpenStandardInput())
                       .WithOutput(Console.OpenStandardOutput());
                ConfigureServer(options, ideId, logLevel, initialTrace, protocolLogLevel);
            });

            using var preloadCts = new CancellationTokenSource();
            var scopeManager = server.Services.GetRequiredService<ILspWorkspaceScopeManager>();
            var logger       = server.Services.GetRequiredService<IIdeSupportLogger>();
            var preloadTask  = ProjectPreloadListener.RunAsync(scopeManager, logger, preloadCts.Token);

            await server.Initialize(CancellationToken.None).ConfigureAwait(false);

            // The real IDE connection is live; the side channel has no further purpose.
            await preloadCts.CancelAsync().ConfigureAwait(false);
            await preloadTask.ConfigureAwait(false);

            await server.WaitForExit.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = ReqnrollLogPaths.ResolveLogDirectory();
                Directory.CreateDirectory(logDir);
                var idePrefix = ideId switch
                {
                    "visualstudio" => "vs",
                    "vscode"       => "vscode",
                    _              => "lsp",
                };
                var logPath = Path.Combine(logDir,
                    $"reqnroll-{idePrefix}-crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(logPath, ex.ToString());
            }
            catch { /* best-effort; never mask the original exception */ }
            throw;
        }
    }

    /// <summary>
    /// Applies the full server configuration (logging, DI graph, capabilities, custom
    /// notifications) to <paramref name="options"/>.  The transport (input/output) is
    /// intentionally NOT set here so that callers can choose it: production uses stdio
    /// (see <see cref="Main"/>); the in-process protocol specs host the server over an
    /// in-memory pipe.
    /// </summary>
    /// <param name="clientIde">
    /// The <c>--ide</c> identifier of the connecting client (e.g. <c>"visualstudio"</c>), or
    /// <see langword="null"/> when absent.  Currently unused by the semantic-token pipeline
    /// (the legend is shared across IDEs); retained for features that may vary behaviour per IDE.
    /// </param>
    /// <param name="logLevel">
    /// The <c>--log-level</c> verbosity requested by the client, defaulting to
    /// <see cref="TraceLevel.Warning"/>. Drives only the file-backed <see cref="IIdeSupportLogger"/>
    /// (our own app-level logging) — deliberately independent of <paramref name="protocolLogLevel"/>.
    /// </param>
    /// <param name="initialTrace">
    /// F41: the LSP protocol trace level (<c>$/logTrace</c>), resolved with the following
    /// precedence — later stages only apply when they actually say something:
    /// <list type="number">
    /// <item><description>this <c>--trace</c> command-line default (defaults to <see cref="InitializeTrace.Off"/>);</description></item>
    /// <item><description><c>InitializeParams.Trace</c>, applied in <c>OnInitialized</c> below —
    /// but only when the client sent something other than <see cref="InitializeTrace.Off"/>, since
    /// that value is indistinguishable from "the client didn't set this field at all" and must not
    /// silently clobber an explicit <c>--trace</c> default;</description></item>
    /// <item><description><c>$/setTrace</c> (<see cref="SetTraceNotificationHandler"/>), which can
    /// set any value — including back to Off — at any time after that.</description></item>
    /// </list>
    /// </param>
    /// <param name="protocolLogLevel">
    /// The <c>--protocol-log-level</c> verbosity for OmniSharp's own internal diagnostics,
    /// defaulting to <see cref="TraceLevel.Warning"/>. Drives the OmniSharp protocol-logging
    /// minimum level, which feeds both the standard <c>window/logMessage</c> notification
    /// (<c>AddLanguageProtocolLogging()</c>, visible to the client) and <see cref="ProtocolLoggerProvider"/>
    /// (a dedicated <c>reqnroll-*-protocol-*.log</c> file, so that content survives even if the
    /// client's Output panel is never inspected). Independent of <paramref name="logLevel"/> —
    /// see that parameter's remarks.
    /// </param>
    internal static void ConfigureServer(LanguageServerOptions options, string? clientIde = null,
        TraceLevel logLevel = TraceLevel.Warning, InitializeTrace initialTrace = InitializeTrace.Off,
        TraceLevel protocolLogLevel = TraceLevel.Warning)
    {
        options.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(ToLogLevel(protocolLogLevel));
            logging.AddLanguageProtocolLogging();
            logging.AddProvider(new ProtocolLoggerProvider(clientIde, protocolLogLevel));
        });

        options.WithServerInfo(new ServerInfo
        {
            Name = "Reqnroll Language Server",
            Version = GetServerVersion()
        });

        // Configure Dependency Injection
        options.Services
            // AddMediatR scans the assembly containing typeof(Program) and registers 
            // all INotificationHandler<T> implementations as transient services.
            // DO NOT add explicit AddSingleton<INotificationHandler<T>> registrations in 
            // AddReqnrollLspHandlers(), as it will cause MediatR to dispatch every 
            // notification to two handler instances (the transient from the scan and 
            // the singleton from the explicit call).
            .AddMediatR(typeof(Program).Assembly)
            // Replaces the IMediator registration AddMediatR just made (last registration wins)
            // with one whose notification fan-out isolates handler faults -- stock MediatR awaits
            // each handler in a bare foreach, so the first to throw suppresses every handler after
            // it, with the casualties decided by assembly-scan order (issue #575). Must stay
            // AFTER AddMediatR; registered before it, AddMediatR's own registration would win.
            .AddTransient<IMediator, ResilientMediator>()
            .AddReqnrollLspCoreServices(clientIde, logLevel, initialTrace)
            .AddReqnrollProjectSystem()
            .AddReqnrollEditorServices()
            .AddReqnrollLspHandlers();

        // Register standard LSP handlers
        options.AddStandardHandlers();

        // Initialize workspace scopes and custom protocol routing
        options.InitializeCustomProtocolRouting();

        options.OnInitialized((languageServer, request, response, ct) =>
        {
            // Each capability is configured by its own named local function below rather than
            // inline, so a mistake in one (e.g. the VS-specific branch in
            // ApplyTextDocumentSyncCapability) can't silently bleed into an unrelated capability
            // assignment sharing the same block.
            ApplyInitialTraceLevel();
            ApplySemanticTokensCapability();
            ApplyStaticInlayHintCapability();
            ApplyStaticFoldingCapability();
            ApplyStaticCodeLensCapability();
            ApplyTextDocumentSyncCapability();
            ApplyRenameCapability();

            return Task.CompletedTask;

            // F41: apply the client's requested trace level over the --trace command-line
            // default, unless the client didn't actually request one. $/setTrace
            // (SetTraceNotificationHandler) can still change the level — including back to
            // Off — at any time after this.
            void ApplyInitialTraceLevel()
            {
                var traceService = languageServer.Services.GetRequiredService<ITraceService>();
                traceService.Level = ResolveInitialTrace(traceService.Level, request.Trace);
            }

            void ApplySemanticTokensCapability()
            {
                // Visual Studio's built-in LSP client can't map our custom token types to a
                // classifier of its own, so tokens for it flow entirely through the separate
                // reqnroll/semanticTokens push mechanism (SemanticTokensPushHandler) plus the VS
                // extension's own SemanticTokensClassificationInterceptor -- neither of which
                // depends on pull support being advertised. Declaring full/range support to VS
                // anyway doesn't help it (it still can't render the result) and isn't free: VS's
                // built-in client has been observed issuing its own pull requests (see
                // SemanticTokensClassificationInterceptor's "fallback path" comment, added
                // defensively for exactly this), duplicating the same expensive full-document
                // encode the push path already paid for, for a response VS then discards.
                //
                // The Legend itself must still be advertised for VS, though: it's not part of the
                // push notification's payload (PublishSemanticTokensParams carries only uri/
                // version/data), so SemanticTokensClassificationInterceptor.CaptureLegendIfPresent
                // reads it out of this same initialize response -- omitting the whole capability
                // for VS would silently break token decoding for the push path too.
                var isVisualStudio = string.Equals(clientIde, "visualstudio", StringComparison.OrdinalIgnoreCase);

                var tokenService = languageServer.Services.GetRequiredService<ISemanticTokensService>();

                response.Capabilities.SemanticTokensProvider = new SemanticTokensRegistrationOptions.StaticOptions
                {
                    Legend = tokenService.Legend,
                    Full = !isVisualStudio,
                    // VS Code's and Rider's built-in LSP clients both support range requests (used as a
                    // large-file/viewport optimization); advertise it since SemanticTokensHandler already
                    // implements textDocument/semanticTokens/range (issue #123). Withheld for VS along
                    // with Full above, per the note at the top of this method.
                    Range = !isVisualStudio
                };
            }

            // inlayHintProvider: declared statically (rather than left to OmniSharp's dynamic
            // client/registerCapability negotiation) because vscode-languageclient's dynamic
            // registration for it races VS Code's restore of previously-open .feature tabs on
            // window load. If the tab renders before the async client/registerCapability round
            // trip completes, VS Code never re-checks for a provider for the rest of the session —
            // closing/reopening the file or opening a different .feature file doesn't recover it. A
            // statically-declared capability is known to the client the instant initialize resolves,
            // so there's no later round trip to lose the race against. See
            // ApplyStaticFoldingCapability for foldingRangeProvider, which hits the same race.
            void ApplyStaticInlayHintCapability()
            {
                response.Capabilities.InlayHintProvider = new InlayHintRegistrationOptions.StaticOptions
                {
                    ResolveProvider = false
                };
            }

            // foldingRangeProvider: declared statically for the same reason as
            // ApplyStaticInlayHintCapability's inlayHintProvider — vscode-languageclient's dynamic
            // client/registerCapability negotiation races VS Code's restore of previously-open
            // .feature tabs on window load, and Rider hit an analogous startup race (#162) where
            // folding stayed empty until the first post-load edit.
            void ApplyStaticFoldingCapability()
            {
                response.Capabilities.FoldingRangeProvider = new FoldingRangeRegistrationOptions.StaticOptions();
            }

            // codeLensProvider.resolveProvider: declared statically for the same
            // dynamic-registration-race reason as inlayHintProvider/foldingRangeProvider above.
            // textDocument/codeLens itself is already always-on for this server (no capability
            // gating needed for the base request); this only advertises that the server CAN
            // service codeLens/resolve — CodeLensResolveHandler does (issue #471).
            //
            // No shipped client currently uses it: neither VS Code nor Rider nor Visual Studio
            // issues codeLens/resolve today, so every lens is still returned fully computed. The
            // decision of whether to hand out an unresolved placeholder lens is NOT made here —
            // it is made per client by ClientIdeContext.SupportsCodeLensResolve, an opt-in
            // allowlist that is deliberately empty (see the note on that allowlist for the
            // evidence and the criteria for adding a client). Advertising resolveProvider while
            // that allowlist is empty is harmless — a spec-compliant client only resolves a lens
            // that arrives without a Command, and none do — and it is what lets a newly
            // allowlisted client work without an initialize-response change.
            void ApplyStaticCodeLensCapability()
            {
                response.Capabilities.CodeLensProvider = new CodeLensRegistrationOptions.StaticOptions
                {
                    ResolveProvider = true
                };
            }

            // vscode-languageclient v10 (used by VS Code and Rider) does not wire its
            // DidChangeTextDocumentFeature when textDocumentSync is absent from the static
            // capabilities — dynamic client/registerCapability for textDocument/didChange is
            // silently ignored and the client never sends content-change notifications.
            // VS's LSP client handles dynamic-only registration correctly, so this static
            // entry is only needed for non-VS clients.
            // Fine-grained selector filtering (*.feature + *.cs) still comes from OmniSharp's
            // dynamic registration once the feature infrastructure is activated.
            void ApplyTextDocumentSyncCapability()
            {
                if (string.Equals(clientIde, "visualstudio", StringComparison.OrdinalIgnoreCase))
                    return;

                response.Capabilities.TextDocumentSync = new TextDocumentSyncOptions
                {
                    Change = TextDocumentSyncKind.Full,
                    OpenClose = true
                };
            }

            // textDocument/prepareRename and textDocument/rename are registered via OnRequest
            // (manual routing) and therefore do NOT automatically populate server capabilities.
            // Without renameProvider, no client (VS Code's vscode-languageclient, or VS's own
            // LSP client) wires its native F2/rename UI to this server, and no rename request
            // ever reaches it.
            // Advertised to every client, including VS: HandleRenameAsync already
            // falls back to plain position-based binding resolution when no
            // reqnroll/selectRenameTarget session is pending, so native F2 in VS works standalone
            // for the common (single, unambiguous binding) case. VS's custom "Reqnroll: Rename
            // Step" command (RenameStepCommand.cs) remains as the only way to disambiguate when a
            // cursor position matches more than one candidate binding — something plain LSP
            // rename has no protocol-level way to prompt for.
            void ApplyRenameCapability()
            {
                response.Capabilities.RenameProvider = new RenameRegistrationOptions.StaticOptions
                {
                    PrepareProvider = true
                };
            }
        });
    }

    /// <summary>
    /// Maps the <see cref="IIdeSupportLogger"/> verbosity scale onto
    /// <see cref="Microsoft.Extensions.Logging.LogLevel"/> for the OmniSharp protocol-logging pipeline.
    /// Delegates to the canonical, shared conversion in <see cref="IdeSupportLogLevelConverter"/> so
    /// there is exactly one <see cref="TraceLevel"/>/<see cref="LogLevel"/> mapping in the codebase.
    /// </summary>
    internal static LogLevel ToLogLevel(TraceLevel level) => IdeSupportLogLevelConverter.ToLogLevel(level);

    /// <summary>
    /// Returns the build-stamped version reported in <c>serverInfo.version</c> during LSP
    /// <c>initialize</c>. Reads <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK
    /// populates from <c>VersionPrefix</c>/<c>VersionSuffix</c> (see build/Version.props) plus the
    /// commit SHA, so it always reflects the build number CI passed via
    /// <c>-p:ReqnrollBuildNumber=...</c> without any further wiring here.
    /// </summary>
    internal static string GetServerVersion()
        => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? "unknown";

    /// <summary>Returns the value following <paramref name="flag"/> in <paramref name="args"/>, or <see langword="null"/> when absent.</summary>
    internal static string? ParseArg(string[] args, string flag)
        => args
            .SkipWhile(a => !string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault();

    /// <summary>Parses <c>--log-level</c> from <paramref name="args"/>, defaulting to <see cref="TraceLevel.Warning"/> when absent or unrecognized.</summary>
    internal static TraceLevel ParseLogLevel(string[] args)
        => Enum.TryParse<TraceLevel>(ParseArg(args, "--log-level"), ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : TraceLevel.Warning;

    /// <summary>Parses <c>--protocol-log-level</c> from <paramref name="args"/>, defaulting to <see cref="TraceLevel.Warning"/> when absent or unrecognized.</summary>
    internal static TraceLevel ParseProtocolLogLevel(string[] args)
        => Enum.TryParse<TraceLevel>(ParseArg(args, "--protocol-log-level"), ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : TraceLevel.Warning;

    /// <summary>Parses <c>--trace</c> (Off/Messages/Verbose) from <paramref name="args"/>, defaulting to <see cref="InitializeTrace.Off"/> when absent or unrecognized.</summary>
    internal static InitializeTrace ParseTraceLevel(string[] args)
        => Enum.TryParse<InitializeTrace>(ParseArg(args, "--trace"), ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : InitializeTrace.Off;

    /// <summary>
    /// F41: resolves the trace level to apply at the initialize handshake. <paramref
    /// name="requested"/> (<c>InitializeParams.Trace</c>) wins whenever the client actually asked
    /// for something; <see cref="InitializeTrace.Off"/> there is indistinguishable from "the
    /// client didn't set this field", so it must not clobber <paramref name="current"/> (the
    /// <c>--trace</c> command-line default).
    /// </summary>
    internal static InitializeTrace ResolveInitialTrace(InitializeTrace current, InitializeTrace requested)
        => requested == InitializeTrace.Off ? current : requested;
}
