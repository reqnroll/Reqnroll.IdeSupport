using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Commenting;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Core.Folding;
using Reqnroll.IdeSupport.LSP.Core.Formatting;
using Reqnroll.IdeSupport.LSP.Core.InlayHints;


using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Features.Commenting;
using Reqnroll.IdeSupport.LSP.Server.Features.Completions;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Server.Features.Folding;
using Reqnroll.IdeSupport.LSP.Server.Features.Formatting;
using Reqnroll.IdeSupport.LSP.Server.Features.InlayHints;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Logging;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Tracing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Extension methods for configuring the Dependency Injection container for the Reqnroll LSP Server.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core infrastructure and cross-cutting services.
    /// </summary>
    public static IServiceCollection AddReqnrollLspCoreServices(this IServiceCollection services, string? clientIde,
        TraceLevel logLevel = TraceLevel.Warning, InitializeTrace initialTrace = InitializeTrace.Off)
    {
        return services
            .AddSingleton(new ClientIdeContext(clientIde, logLevel))
            .AddSingleton<IIdeSupportLogger, LspIdeSupportLogger>()
            // Do NOT also register ILoggerFactory/ILogger<> here: Program.ConfigureServer's
            // options.ConfigureLogging(...) already establishes the real Microsoft.Extensions.Logging
            // pipeline (SetMinimumLevel, AddLanguageProtocolLogging, ProtocolLoggerProvider) in this
            // same IServiceCollection, before this method runs. A later AddSingleton<ILoggerFactory>
            // here would win the last-registration-wins resolution and silently replace it — which
            // is exactly what happened here originally: OmniSharp-internal ILogger<T> messages leaked
            // into the app-level "server" log file gated by --log-level instead of their own
            // "protocol" file gated by the independent --protocol-log-level. Any new code that wants
            // ILogger<T> gets the correctly-configured one for free from that existing pipeline.
            .AddSingleton<IIdeScope, LspIdeScope>()
            // Same singleton instance as IIdeScope.FileSystem, exposed directly so services that
            // only need file-system access don't have to depend on the broader IIdeScope contract.
            .AddSingleton<IFileSystemForIDE>(sp => sp.GetRequiredService<IIdeScope>().FileSystem)
            // Telemetry: emit telemetry/event notifications, optionally mirrored to a local JSONL
            // debug log (REQNROLL_TELEMETRY_DEBUG_LOG). The decorator wraps the real emitter; when
            // the debug log is unconfigured the sink is a no-op and it simply forwards.
            .AddSingleton<ITelemetryDebugLog>(_ => TelemetryDebugLog.FromEnvironment())
            .AddSingleton<LspTelemetryService>()
            .AddSingleton<ILspTelemetryService>(sp => new FileLoggingLspTelemetryService(
                sp.GetRequiredService<LspTelemetryService>(),
                sp.GetRequiredService<ITelemetryDebugLog>()))
            // ITelemetryService is the VS-host-lifecycle contract; LspErrorTelemetryService only
            // meaningfully implements MonitorError (forwarded to ILspTelemetryService as an "Error"
            // telemetry/event) — every other member is a no-op (VS/host-UI-only concerns the server
            // has no equivalent of). Previously registered as NullLspTelemetryService, which silently
            // dropped LSP.Core exceptions (e.g. Gherkin parse errors) — issue #255.
            .AddSingleton<ITelemetryService>(sp => new LspErrorTelemetryService(
                sp.GetRequiredService<ILspTelemetryService>()))
            // IErrorTelemetryService is the actual LSP.Core-facing contract (DeveroomGherkinParser,
            // DeveroomTagParser, CompletionContextResolver depend on this narrow interface, not the
            // full ITelemetryService above) — same singleton, resolved via interface inheritance
            // (issue #255/#259's ISP split).
            .AddSingleton<IErrorTelemetryService>(sp => sp.GetRequiredService<ITelemetryService>())
            .AddSingleton<IDeveroomConfigurationProvider, ProjectSystemDeveroomConfigurationProvider>()
            .AddSingleton<IEditorConfigOptionsProvider>(sp =>
                new FileSystemEditorConfigOptionsProvider(sp.GetRequiredService<IIdeScope>().FileSystem))
            // Performance Verification, Layer 4: field instrumentation. The recorder writes
            // PERF lines to the log and (when REQNROLL_PERF_TELEMETRY_SAMPLE is set) emits sampled
            // PerfSample telemetry. Singleton so the sampler's RNG is shared across handlers.
            .AddSingleton<IPerformanceTelemetrySampler>(_ => PerformanceTelemetrySampler.FromEnvironment())
            // F41: tracks the LSP `trace` level (--trace / InitializeParams.Trace / $/setTrace) and
            // issues $/logTrace notifications. Singleton so the level set by $/setTrace is visible
            // to every consumer (currently OperationDurationRecorder's PERF lines).
            .AddSingleton<ITraceService>(sp => new TraceService(
                sp.GetRequiredService<OmniSharp.Extensions.LanguageServer.Protocol.Server.ILanguageServerFacade>(),
                initialTrace))
            .AddSingleton<IOperationDurationRecorder>(sp => new OperationDurationRecorder(
                sp.GetRequiredService<IIdeSupportLogger>(),
                sp.GetRequiredService<ClientIdeContext>(),
                sp.GetRequiredService<ILspTelemetryService>(),
                sp.GetRequiredService<IPerformanceTelemetrySampler>(),
                sp.GetRequiredService<ITraceService>()));
    }

    /// <summary>
    /// Registers project system, workspace, and binding discovery services.
    /// </summary>
    public static IServiceCollection AddReqnrollProjectSystem(this IServiceCollection services)
    {
        return services
            .AddSingleton<ILspWorkspaceScopeManager, LspWorkspaceScopeManager>()
            // BindingRegistryProviderRouter creates and owns one ConnectorBindingRegistryProvider
            // per project and routes binding-registry lookups to the correct per-project instance
            // via IProjectBindingRegistryLookup.GetRegistryForUri. Registries are NOT merged —
            // each feature file is resolved against only its own project's bindings.
            .AddSingleton<BindingRegistryProviderRouter>()
            .AddSingleton<IProjectBindingRegistryLookup>(sp => sp.GetRequiredService<BindingRegistryProviderRouter>())
            // Roslyn/C# source-level binding discovery for .cs edits.
            .AddSingleton<ICSharpBindingDiscoveryService, CSharpBindingDiscoveryService>()
            // Scenario -> generated-test-method mapping layer (design doc §3, issue #262).
            .AddSingleton<IScenarioTestTargetResolver, ScenarioTestTargetResolver>()
            .AddSingleton<IDeveroomTagParser, DeveroomTagParser>()
            // Debounces the closed-feature-file rescan triggered by an incremental Roslyn patch
            // whose binding expressions actually changed (BindingRegistryChangedHandler).
            .AddSingleton<IFeatureRescanDebouncer, FeatureRescanDebouncer>()
            // Debounces MatchCacheChangedNotification-driven client refreshes (code lens, semantic
            // tokens, inlay hints). Must be a singleton, unlike the MediatR handlers that use it —
            // see IRefreshDebouncer's remarks for why a transient handler's own instance field
            // can't debounce anything.
            .AddSingleton<IRefreshDebouncer, RefreshDebouncer>()
            // Gets didOpen/didChange's own reparse off the shared Serial dispatch lane while
            // preserving correctness for pull handlers with no refresh capability (issue #471) —
            // see IFeatureParseCoordinator's remarks. Singleton for the same reason as
            // IRefreshDebouncer above: the per-URI pending-work state must outlive any single
            // transient handler instance.
            .AddSingleton<IFeatureParseCoordinator, FeatureParseCoordinator>();
    }

    /// <summary>
    /// Registers editor services, parsing, and shared caching components.
    /// </summary>
    public static IServiceCollection AddReqnrollEditorServices(this IServiceCollection services)
    {
        return services
            .AddSingleton<IDocumentBufferService, DocumentBufferService>()
            .AddSingleton<ICSharpFileTextCache, CSharpFileTextCache>()
            // Shared cache of parsed C# syntax trees, keyed by file path (issue #491) — avoids
            // re-parsing the same unchanged file (e.g. a generated <feature>.feature.cs code-behind,
            // or a step-definition file with several attributes on one method) once per caller within
            // the same logical operation. Consumed by ScenarioTestTargetResolver and
            // CSharpAttributeLiteralResolver.
            .AddSingleton<ICSharpSyntaxTreeCache, CSharpSyntaxTreeCache>()
            // BindingMatchService holds the per-document match cache; it must be a singleton
            // so the cache survives across requests and is shared by the tagger (writer) and
            // the Go to Definition / diagnostics consumers (readers).
            .AddSingleton<IBindingMatchService, BindingMatchService>()
            .AddSingleton<IGherkinDocumentTaggerService, GherkinDocumentTaggerService>()
            .AddSingleton<ISemanticTokensService, SemanticTokensService>()
            .AddSingleton<IDiagnosticsAggregator, DiagnosticsAggregator>()
            .AddSingleton<ICSharpDiagnosticsAggregator, CSharpDiagnosticsAggregator>()
            .AddSingleton<ICSharpDiagnosticsPublisher, CSharpDiagnosticsPublisher>()
            .AddSingleton<IFoldingRangeService, FoldingRangeService>()
            .AddSingleton<ICommentToggleService, CommentToggleService>()
            .AddSingleton<IInlayHintService, InlayHintService>()
            .AddSingleton<IGherkinDocumentFormatter, GherkinDocumentFormatter>();
    }

    /// <summary>
    /// Registers OmniSharp LSP protocol handlers as singletons.
    /// 
    /// NOTE: MediatR notification handlers (e.g., SemanticTokensRefreshHandler, DiagnosticsPublishHandler) 
    /// are auto-discovered and registered as transients by the AddMediatR call in ConfigureServer. 
    /// DO NOT add explicit AddSingleton&lt;INotificationHandler&lt;T&gt;&gt; registrations here, as it will 
    /// cause MediatR to dispatch every notification to two handler instances (the transient from the 
    /// scan and the singleton from the explicit call). 
    /// 
    /// The handlers listed below ARE registered explicitly as singletons because OmniSharp resolves 
    /// them from the root DryIoc container (not from a per-request scope), and they hold injected 
    /// ILanguageServer references that must live for the server's lifetime.
    /// </summary>
    public static IServiceCollection AddReqnrollLspHandlers(this IServiceCollection services)
    {
        return services
            .AddSingleton<TextDocumentSyncHandler>()
            .AddSingleton<WorkspaceFoldersHandler>()
            .AddSingleton<WatchedFilesHandler>()
            .AddSingleton<SemanticTokensHandler>()
            .AddSingleton<ReferencesHandler>()
            .AddSingleton<FindStepUsagesHandler>()
            .AddSingleton<ICompletionContextResolver, CompletionContextResolver>()
            .AddSingleton<ICompletionService, CompletionService>()
            .AddSingleton<ICompletionMatcher, ReturnAllCompletionMatcher>()
            .AddSingleton<DefinitionHandler>()
            .AddSingleton<GoToHooksHandler>()
            .AddSingleton<GoToMatchingScenariosHandler>()
            .AddSingleton<ResolveTestTargetsHandler>()
            .AddSingleton<StepCodeLensHandler>()
            .AddSingleton<HookCodeLensHandler>()
            .AddSingleton<HookMatchCountCodeLensHandler>()
            .AddSingleton<CodeLensResolveHandler>()
            .AddSingleton<CompletionHandler>()
            .AddSingleton<IStepScaffoldService, StepScaffoldService>()
            .AddSingleton<CodeActionHandler>()
            .AddSingleton<IFindUnusedStepDefinitionsService, FindUnusedStepDefinitionsService>()
            .AddSingleton<FindUnusedStepDefinitionsHandler>()
            .AddSingleton<DocumentActivatedHandler>()
            .AddSingleton<FormattingHandler>()
            .AddSingleton<IDocumentSymbolService, DocumentSymbolService>()
            .AddSingleton<DocumentSymbolHandler>()
            .AddSingleton<FoldingRangeHandler>()
            .AddSingleton<CommentToggleHandler>()
            .AddSingleton<RenameHandler>()
            .AddSingleton<RenameSessionManager>()
            .AddSingleton<RenameBindingResolver>()
            .AddSingleton<CSharpAttributeLiteralResolver>()
            .AddSingleton<RenameTargetsHandler>()
            .AddSingleton<InlayHintHandler>()
            .AddSingleton<SetTraceNotificationHandler>();
    }
}
