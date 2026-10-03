using System.Reflection;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Tracing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.Common.Lsp;

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
            var logger = server.Services.GetRequiredService<IIdeSupportLogger>();
            var preloadTask = ProjectPreloadListener.RunAsync(scopeManager, logger, preloadCts.Token);

            // Issue #582: periodic feature-usage flush, running for the whole server lifetime
            // alongside the request-handling pipeline (a no-op loop when its env var is unset —
            // see FeatureUsageFlushService.RunAsync). Started here, not lazily on first use, so a
            // session with a long-idle start still gets a correctly-timed first window.
            using var usageFlushCts = new CancellationTokenSource();
            var usageFlushService = server.Services.GetRequiredService<IFeatureUsageFlushService>();
            var usageFlushTask = usageFlushService.RunAsync(usageFlushCts.Token);

            // The final flush rides the LSP shutdown request, not process exit: by the time
            // WaitForExit completes the client has sent `exit` and the transport may be closed,
            // which would drop the telemetry/event notification.
            using var usageFlushOnShutdown = FeatureUsageFlushService.FlushOnShutdown(usageFlushService, server.Shutdown);

            // Issue #845: ServerSessionEnded, subscribed after the flush above so the final
            // FeatureUsageSummary precedes it. Once-only, so the post-exit fallback below cannot double-send.
            var sessionTelemetry = server.Services.GetRequiredService<ServerSessionTelemetry>();
            using var sessionEndedOnShutdown = sessionTelemetry.EndOnShutdown(server.Shutdown);

            await server.Initialize(CancellationToken.None).ConfigureAwait(false);

            // The real IDE connection is live; the side channel has no further purpose.
            await preloadCts.CancelAsync().ConfigureAwait(false);
            await preloadTask.ConfigureAwait(false);

            await server.WaitForExit.ConfigureAwait(false);

            // Stop the periodic loop. The final flush already ran on the shutdown request above;
            // the call below is a fallback for an `exit` without a preceding `shutdown`, and is
            // silent when the counters are already drained. Neither is reached on a force-quit,
            // IDE crash, or OS shutdown; see FeatureUsageFlushService's remarks on the loss profile.
            await usageFlushCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await usageFlushTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: RunAsync's own Task.Delay observes the cancellation above.
            }
            await usageFlushService.FlushFinalAsync().ConfigureAwait(false);
            sessionTelemetry.ReportEnded();
        }
        catch (Exception ex)
        {
            try
            {
                // Reuses SynchronousFileLogger itself (role "crash") rather than one-off
                // directory/filename/formatting code (issue #628): same
                // reqnroll-{ide}-{role}-{date}-{pid}.log grammar and canonical preamble as every
                // other file in the family, instead of a bespoke reqnroll-{ide}-crash-{date-time}.log
                // with no PID and a raw ex.ToString() dump.
                var idePrefix = IdeLogPrefix.From(ideId);
                new SynchronousFileLogger(idePrefix, "crash", TraceLevel.Error)
                    .LogException(ex, "Unhandled exception - LSP server terminating");
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
    /// <see langword="null"/> when absent.  Seeds the DI-registered
    /// <see cref="ClientIdeContext"/> singleton that every per-IDE branch reads, and is therefore
    /// the server's primary identity source.  It is not the only one: when it is absent,
    /// <c>OnInitialized</c> falls back to the <c>InitializeParams.ClientInfo</c> the client
    /// self-reports over the wire (issue #709) — see <see cref="ApplyClientInfo"/>.
    /// <see cref="ApplySemanticTokensCapability"/> resolves VS-ness through that context rather than
    /// from this argument directly, so the advertised capability and the handler that later pushes
    /// tokens can never disagree about which client is connected.
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

            // Issue #660: OmniSharp's own LanguageServerLoggingManager (registered internally for
            // every LanguageServer, regardless of anything configured here) is an
            // IPostConfigureOptions<LoggerFilterOptions> that unconditionally overwrites
            // LoggerFilterOptions.MinLevel from the current $/setTrace level (Off/Messages/Verbose).
            // IPostConfigureOptions always runs after every IConfigureOptions regardless of
            // registration order, so it silently discards whatever SetMinimumLevel just set above --
            // no reordering of the calls in this method can fix that. PostConfigure only ever
            // reassigns MinLevel, though; it never touches Rules, so an explicit provider-scoped
            // LoggerFilterRule survives it and takes priority over MinLevel for that provider
            // (confirmed against OmniSharp.Extensions.LanguageServer 0.19.9 by decompiling
            // LanguageServerLoggingManager and LanguageServerLoggerExtensions.AddLanguageProtocolLogging).
            // LanguageServerLoggerProvider (the window/logMessage sink) is internal to OmniSharp and
            // so can't be named via the generic AddFilter<T>() overload -- the rule below is built
            // from its known full type name instead, which LoggerRuleSelector matches the same way.
            logging.Services.Configure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(
                "OmniSharp.Extensions.LanguageServer.Server.Logging.LanguageServerLoggerProvider",
                categoryName: null,
                logLevel: ToLogLevel(protocolLogLevel),
                filter: null)));
            logging.AddFilter<ProtocolLoggerProvider>(category: null, level: ToLogLevel(protocolLogLevel));
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

        // Issue #845: ServerSessionStarted once the LSP handshake has completed (OnStarted runs
        // after `initialized`, so ClientIdeContext already carries the client's self-reported version).
        options.OnStarted((languageServer, _) =>
        {
            languageServer.Services.GetRequiredService<ServerSessionTelemetry>().ReportStarted();
            return Task.CompletedTask;
        });

        options.OnInitialized((languageServer, request, response, ct) =>
        {
            // Each capability is configured by its own named local function below rather than
            // inline, so a mistake in one (e.g. an IDE-specific branch in
            // ApplySemanticTokensCapability) can't silently bleed into an unrelated capability
            // assignment sharing the same block.
            ApplyInitialTraceLevel();
            ApplyClientIdentity();
            ApplySemanticTokensCapability();
            ApplyStaticInlayHintCapability();
            ApplyStaticFoldingCapability();
            ApplyStaticCodeLensCapability();
            ApplyTextDocumentSyncCapability();
            ApplyRenameCapability();
            ApplyTestOutcomesCapability();
            ApplyCustomProtocolCapabilities();

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

            // Issue #709: record the identity the client self-reports over the wire, and let it fill
            // in the IDE when --ide was absent. ClientInfo is the only identity signal that travels
            // in the protocol itself; it arrives here, long after the DI container (and the
            // ClientIdeContext that --ide seeded) was built, which is why ClientIdeContext has to be
            // mutable at this one point rather than resolved from the argument alone.
            //
            // Ordered first among the Apply* calls: ApplySemanticTokensCapability resolves VS-ness
            // through ClientIdeContext (see its note), and the identity log line below is most useful
            // when it precedes the capability decisions it explains. Nothing else here depends on it,
            // because every other per-IDE branch in the server reads ClientIdeContext lazily, per
            // request, which is always after this point.
            void ApplyClientIdentity()
            {
                var ideContext = languageServer.Services.GetRequiredService<ClientIdeContext>();
                ideContext.ApplyClientInfo(request.ClientInfo);

                // Logged at Info: at the default --log-level Warning this line is suppressed, which is
                // deliberate (a normal session shouldn't grow a log line it never needs), so diagnosing
                // a misidentified client means re-running with --log-level Info. See CONTRIBUTING.md's
                // "Server logging and trace verbosity".
                languageServer.Services.GetRequiredService<IIdeSupportLogger>().LogInfo(
                    $"Client identity: --ide={Describe(ideContext.IdeArgument)}, "
                    + $"clientInfo={Describe(ideContext.ClientName)}"
                    + (ideContext.ClientVersion is null ? string.Empty : $" ({ideContext.ClientVersion})")
                    + $", effective ide={Describe(ideContext.Ide)}"
                    + (ideContext.IdeResolvedFromClientInfo ? " (resolved from ClientInfo)" : string.Empty));

                static string Describe(string? value) => string.IsNullOrEmpty(value) ? "<none>" : value;
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
                //
                // Resolved through ClientIdeContext rather than the raw clientIde argument (issue
                // #709) so this agrees with every other per-IDE branch in the server: the push
                // handler, CodeActionHandler, CompletionHandler, RenameHandler and
                // CodeLensRefreshRequester all read ClientIdeContext, and it is the only one of the
                // two that knows the identity resolved from ClientInfo when --ide was absent.
                // ApplyClientIdentity runs first, above, so the fallback is already applied here.
                var requiresPushedSemanticTokens = languageServer.Services
                    .GetRequiredService<ClientIdeContext>().Facets.RequiresPushedSemanticTokens;

                var tokenService = languageServer.Services.GetRequiredService<ISemanticTokensService>();

                response.Capabilities.SemanticTokensProvider = new SemanticTokensRegistrationOptions.StaticOptions
                {
                    Legend = tokenService.Legend,
                    Full = !requiresPushedSemanticTokens,
                    // VS Code's and Rider's built-in LSP clients both support range requests (used as a
                    // large-file/viewport optimization); advertise it since SemanticTokensHandler already
                    // implements textDocument/semanticTokens/range (issue #123). Withheld for VS along
                    // with Full above, per the note at the top of this method.
                    Range = !requiresPushedSemanticTokens
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
            // it is made per client by ClientFacets.SupportsCodeLensResolve, an opt-in
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

            // Advertised statically to every client:
            // - vscode-languageclient v10 (VS Code, Rider) does not wire its
            //   DidChangeTextDocumentFeature when textDocumentSync is absent from the static
            //   capabilities: a dynamic-only registration is silently ignored.
            // - Visual Studio (issue #800) handles dynamic registration, but only for documents it
            //   attaches after the client/registerCapability arrives (~100 ms after `initialized`).
            //   A document it attached before that (a restored tab, or the open file after a
            //   solution switch) never got didOpen or didChange for the whole session. VS used to
            //   be excluded here on the assumption that dynamic-only was enough.
            // Fine-grained selector filtering (*.feature + *.cs) still comes from OmniSharp's
            // dynamic registration once the feature infrastructure is activated.
            void ApplyTextDocumentSyncCapability()
            {
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

            // Advertises the custom LSP-server outcome pipeline as a typed, top-level sibling of
            // the spec's own capability fields — `reqnrollTestOutcomesProvider`, keyed and shaped
            // by ReqnrollTestOutcomesOptions, not nested under `experimental` — mirroring the wire
            // shape Roslyn's own LSP server uses for its custom capabilities.
            //
            // Not a real property on a ServerCapabilities subclass, though: InitializeResult.
            // Capabilities is init-only (OmniSharp record type), and OnInitialized hands us an
            // already-constructed InitializeResult with no way to swap what object that property
            // points to — only to mutate the existing instance's own settable members. So this
            // goes through ServerCapabilities.ExtensionData, OmniSharp's own [JsonExtensionData]
            // catch-all, which both writes an extra top-level property during serialization AND
            // (for a peer with no matching Reqnroll type of its own) captures an unknown one
            // losslessly on the way in — the same "ignored, not dropped" safety `experimental`
            // relied on, just without forcing every value into the same shared dictionary key.
            void ApplyTestOutcomesCapability()
            {
                response.Capabilities.ExtensionData ??= new Dictionary<string, JToken>();
                response.Capabilities.ExtensionData["reqnrollTestOutcomesProvider"] = JObject.FromObject(
                    new ReqnrollTestOutcomesOptions
                    {
                        RegisterRunMethod = CustomLspMethodNames.ReqnrollRegisterTestRun,
                        GetOutcomeMethod = CustomLspMethodNames.ReqnrollGetTestOutcome,
                        ChangedNotification = CustomLspMethodNames.ReqnrollTestOutcomesChanged,
                    });
            }

            // Advertises the rest of this server's custom reqnroll/* protocol surface -- every
            // method registered via manual OnRequest/OnNotification routing in
            // InitializeCustomProtocolRouting that isn't already covered by one of the Apply*
            // functions above -- as typed, top-level entries in ServerCapabilities.ExtensionData
            // rather than leaving them undeclared. See ReqnrollMethodProvider's remarks for why
            // this is documentation (the initialize response as a readable manifest, backed by a
            // regression test per entry) rather than feature-detection: every one of these methods
            // has existed since this server's first version, so no client conditionally branches on
            // whether one is present.
            void ApplyCustomProtocolCapabilities()
            {
                response.Capabilities.ExtensionData ??= new Dictionary<string, JToken>();
                var extensionData = response.Capabilities.ExtensionData;

                extensionData["reqnrollWorkspaceLifecycleProvider"] = JObject.FromObject(new ReqnrollWorkspaceLifecycleOptions
                {
                    ProjectLoadedMethod = CustomLspMethodNames.ReqnrollProjectLoaded,
                    ProjectUnloadedMethod = CustomLspMethodNames.ReqnrollProjectUnloaded,
                    ProjectFilesMethod = CustomLspMethodNames.ReqnrollProjectFiles,
                });

                extensionData["reqnrollFindStepUsagesProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollFindStepUsages });

                extensionData["reqnrollFindHooksProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollFindHooks });

                extensionData["reqnrollFindStepDefinitionsProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollFindStepDefinitions });

                extensionData["reqnrollFindMatchingScenariosProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollFindMatchingScenarios });

                extensionData["reqnrollResolveTestTargetsProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollResolveTestTargets });

                extensionData["reqnrollFindUnusedStepDefinitionsProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollFindUnusedStepDefinitions });

                extensionData["reqnrollStepRenameProvider"] = JObject.FromObject(new ReqnrollStepRenameOptions
                {
                    RenameTargetsMethod = CustomLspMethodNames.ReqnrollRenameTargets,
                    SelectRenameTargetMethod = CustomLspMethodNames.ReqnrollSelectRenameTarget,
                    RenameAppliedMethod = CustomLspMethodNames.ReqnrollRenameApplied,
                });

                extensionData["reqnrollRefreshCodeLensProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollRefreshCodeLens });

                extensionData["reqnrollSemanticTokensPushProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollSemanticTokens });

                extensionData["reqnrollDocumentSymbolHierarchicalProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical });

                extensionData["reqnrollDocumentActivatedProvider"] = JObject.FromObject(
                    new ReqnrollMethodProvider { Method = CustomLspMethodNames.ReqnrollDocumentActivated });
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
