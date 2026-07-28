using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Features.Completions;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefs;
using Reqnroll.IdeSupport.LSP.Server.Features.Folding;
using Reqnroll.IdeSupport.LSP.Server.Features.Formatting;
using Reqnroll.IdeSupport.LSP.Server.Features.InlayHints;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Tracing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Extension methods for configuring custom protocol handlers on <see cref="LanguageServerOptions"/>.
/// </summary>
public static class LanguageServerOptionsExtensions
{
    /// <summary>
    /// <see cref="JObject.FromObject(object)"/>'s parameterless overload serializes with
    /// Newtonsoft's global default settings — PascalCase property names, not the lowercase
    /// camelCase every other LSP payload on the wire uses. That silently produced e.g.
    /// <c>"Start"</c>/<c>"Character"</c> instead of <c>"start"</c>/<c>"character"</c> for plain
    /// ranges, tolerated by clients doing case-insensitive property matching — but it breaks
    /// <see cref="RangeOrPlaceholderRange"/>'s union-type discrimination outright: its converter
    /// looks for the exact lowercase keys <c>"range"</c>/<c>"placeholder"</c>/
    /// <c>"defaultBehavior"</c> to tell which variant is present, and silently produces neither
    /// on the read side when the write side used PascalCase (issue #33 follow-up).
    /// </summary>
    private static readonly JsonSerializer CamelCaseSerializer = JsonSerializer.Create(
        new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });

    /// <summary>
    /// Registers standard OmniSharp handlers. 
    /// </summary>
    public static void AddStandardHandlers(this LanguageServerOptions options)
    {
        options.AddHandler<TextDocumentSyncHandler>()
               .AddHandler<WorkspaceFoldersHandler>()
               .AddHandler<WatchedFilesHandler>()
               .AddHandler<DefinitionHandler>()
               .AddHandler<CompletionHandler>()
               .AddHandler<CodeActionHandler>()
               .AddHandler<FormattingHandler>()
               .AddHandler<DocumentSymbolHandler>()
               // F41: standard $/setTrace notification, letting the client change the trace
               // level at runtime.
               .AddHandler<SetTraceNotificationHandler>();
    }

    /// <summary>
    /// Seeds workspace scopes and configures custom Reqnroll protocol routing.
    /// 
    /// Because OmniSharp's LSP parameter types (e.g., ReferenceParams, SemanticTokensParams) 
    /// do not implement MediatR's IRequest, we must use OnRequest/OnNotification delegates 
    /// for custom method routing. This method encapsulates the service resolution in a 
    /// strongly-typed resolver initialized during OnStarted, eliminating nullable IServiceProvider 
    /// captures and null-forgiving operators.
    /// </summary>
    public static void InitializeCustomProtocolRouting(this LanguageServerOptions options)
    {
        // Encapsulate service resolution to avoid nullable IServiceProvider? anti-pattern
        LspHandlerResolver? resolver = null;

        options.OnStarted((languageServer, _) =>
        {
            resolver = new LspHandlerResolver(languageServer.Services);
            
            var scopeManager = resolver.Get<ILspWorkspaceScopeManager>();
            if (languageServer.ClientSettings.WorkspaceFolders is not null)
            {
                foreach (var folder in languageServer.ClientSettings.WorkspaceFolders)
                {
                    var path = folder.Uri.GetFileSystemPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        scopeManager.OpenWorkspace(path);
                    }
                }
            }

            return Task.CompletedTask;
        });

        // ── Custom client-to-server notifications (now using MediatR INotification) ─
        options.OnNotification<ReqnrollProjectLoadedParams>(
            LspMethodNames.ReqnrollProjectLoaded,
            (p, ct) => resolver!.Get<ILspWorkspaceScopeManager>().HandleProjectLoadedAsync(p, ct));

        options.OnNotification<ReqnrollProjectUnloadedParams>(
            LspMethodNames.ReqnrollProjectUnloaded,
            (p, ct) => resolver!.Get<ILspWorkspaceScopeManager>().HandleProjectUnloadedAsync(p, ct));

        options.OnNotification<ReqnrollProjectFilesParams>(
            LspMethodNames.ReqnrollProjectFiles,
            (p, ct) => resolver!.Get<ILspWorkspaceScopeManager>().HandleProjectFilesAsync(p, ct));

        // #85: VS-side tab-activation backstop — forces a fresh binding-match recompute and
        // diagnostics/semantic-tokens republish for a document the client has just detected
        // becoming the active tab, independent of whatever normally triggers that.
        options.OnNotification<DocumentActivatedParams>(
            LspMethodNames.ReqnrollDocumentActivated,
            (p, ct) => resolver!.Get<DocumentActivatedHandler>().HandleAsync(p, ct));

        // ── Manual request routing to bypass dynamic registration limitations ─────────
        // semanticTokens/full is an interactive performance target; wrap it (and its delta sibling)
        // through MeasuredAsync(...) so Layer 4 field instrumentation times the manual-route handler
        // at one site. The same helper can wrap the other manual routes below as needed.
        options.OnRequest<SemanticTokensParams, SemanticTokens>(
            LspMethodNames.TextDocumentSemanticTokensFull,
            (request, ct) => MeasuredAsync(resolver!, LspMethodNames.TextDocumentSemanticTokensFull,
                request.TextDocument.Uri, () => resolver!.Get<SemanticTokensHandler>().HandleAsync(request, ct)));

        options.OnRequest<SemanticTokensDeltaParams, SemanticTokensFullOrDelta>(
            LspMethodNames.TextDocumentSemanticTokensFullDelta,
            (request, ct) => MeasuredAsync(resolver!, LspMethodNames.TextDocumentSemanticTokensFullDelta,
                request.TextDocument.Uri, () => resolver!.Get<SemanticTokensHandler>().HandleAsync(request, ct)));

        options.OnRequest<SemanticTokensRangeParams, SemanticTokens>(
            LspMethodNames.TextDocumentSemanticTokensRange,
            (request, ct) => MeasuredAsync(resolver!, LspMethodNames.TextDocumentSemanticTokensRange,
                request.TextDocument.Uri, () => resolver!.Get<SemanticTokensHandler>().HandleAsync(request, ct)));

        options.OnRequest<ReferenceParams, LocationOrLocationLinks>(
            LspMethodNames.TextDocumentReferences,
            (request, ct) => resolver!.Get<StepReferencesHandler>().HandleAsync(request, ct));

        options.OnRequest<ReferenceParams, FindStepUsagesResponse>(
            LspMethodNames.ReqnrollFindStepUsages,
            (request, ct) => resolver!.Get<FindStepUsagesHandler>().HandleAsync(request, ct));

        // Always-hierarchical documentSymbol for the VS extension's own Navigation Bar (Issue #5
        // / Navigation Bar symbol source design, Option B) — distinct from
        // textDocument/documentSymbol, whose shape depends on the
        // real client's declared hierarchicalDocumentSymbolSupport capability. See remarks on
        // DocumentSymbolHandler.HandleHierarchicalAsync.
        options.OnRequest<DocumentSymbolParams, IReadOnlyList<DocumentSymbol>>(
            LspMethodNames.ReqnrollDocumentSymbolHierarchical,
            (request, ct) => resolver!.Get<DocumentSymbolHandler>().HandleHierarchicalAsync(request, ct));

        options.OnRequest<TextDocumentPositionParams, GoToStepDefinitionsResponse>(
            LspMethodNames.ReqnrollGoToStepDefinitions,
            (request, ct) => resolver!.Get<GoToStepDefinitionsHandler>().HandleAsync(request, ct));

        options.OnRequest<GoToHooksParams, GoToHooksResponse>(
            LspMethodNames.ReqnrollGoToHooks,
            (request, ct) => resolver!.Get<GoToHooksHandler>().HandleAsync(request, ct));

        // A single manual registration handles textDocument/codeLens for both file kinds:
        // StepCodeLensHandler owns .cs (step usages), HookCodeLensHandler owns .feature (hook
        // matches, issue #269) -- each returns an empty array for URIs it doesn't own, so
        // concatenating is safe and avoids a second competing OnRequest for the same method.
        options.OnRequest<CodeLensParams, CodeLens[]>(
            LspMethodNames.TextDocumentCodeLens,
            async (request, ct) =>
            {
                var stepLenses = await resolver!.Get<StepCodeLensHandler>().HandleAsync(request, ct);
                var hookLenses = await resolver!.Get<HookCodeLensHandler>().HandleAsync(request, ct);
                return stepLenses.Length == 0 ? hookLenses
                    : hookLenses.Length == 0 ? stepLenses
                    : stepLenses.Concat(hookLenses).ToArray();
            });

        // inlayHint/foldingRange are routed manually (rather than via AddHandler's dynamic
        // registration) so that inlayHintProvider/foldingRangeProvider can be declared
        // statically in the initialize response — see Program.ConfigureServer for why.
        options.OnRequest<InlayHintParams, InlayHintContainer?>(
            LspMethodNames.TextDocumentInlayHint,
            (request, ct) => resolver!.Get<InlayHintHandler>().HandleAsync(request, ct));

        options.OnRequest<FoldingRangeRequestParam, Container<FoldingRange>?>(
            LspMethodNames.TextDocumentFoldingRange,
            (request, ct) => resolver!.Get<FoldingRangeHandler>().HandleAsync(request, ct));

        options.OnRequest<FindUnusedStepDefinitionsParams, FindUnusedStepDefinitionsResponse>(
            LspMethodNames.ReqnrollFindUnusedStepDefinitions,
            (_, ct) => resolver!.Get<FindUnusedStepDefinitionsHandler>().HandleAsync(ct));

        // ── Step Rename refactoring ──────────────────────────────────────────────
        // prepareRename null → null: per the LSP spec, a null prepareRename result means
        // "rename not supported at the given position", and vscode-languageclient handles that
        // quietly. Throwing here instead surfaces the raw exception text as a visible error
        // popup — confusing UX for what should be a silent no-op (see issue #47).
        // rename null → empty WorkspaceEdit: VS Code treats a JSON-RPC error from rename as
        // "Internal Error" (confusing UX); returning an empty edit is a silent no-op fallback.
        // renameTargets null → empty response.
        //
        // Response type is JToken, not LspRange?: OmniSharp's manual OnRequest routing
        // (DelegatingRequestHandler<T, TResponse>.Handle) always calls
        // JToken.FromObject((object)response, ...) with no null-check, so any manual route that
        // completes with a null TResponse throws ArgumentNullException from inside Newtonsoft —
        // regardless of TResponse's declared nullability. Returning JValue.CreateNull() (a
        // non-null JToken that *represents* JSON null) instead of a null reference sidesteps that
        // library bug while still round-tripping as a null prepareRename result on the wire.
        options.OnRequest<PrepareRenameParams, JToken>(
            LspMethodNames.TextDocumentPrepareRename,
            async (request, ct) =>
            {
                var result = await resolver!.Get<StepRenameHandler>().HandlePrepareRenameAsync(request, ct);
                return result != null ? (JToken)JObject.FromObject(result, CamelCaseSerializer) : JValue.CreateNull();
            });

        options.OnRequest<RenameParams, WorkspaceEdit>(
            LspMethodNames.TextDocumentRename,
            async (request, ct) =>
                await resolver!.Get<StepRenameHandler>().HandleRenameAsync(request, ct)
                ?? new WorkspaceEdit());

        options.OnRequest<TextDocumentPositionParams, RenameTargetsResponse>(
            LspMethodNames.ReqnrollRenameTargets,
            async (request, ct) =>
                await resolver!.Get<RenameTargetsHandler>().HandleRenameTargetsAsync(request, ct)
                ?? new RenameTargetsResponse());

        options.OnNotification<SelectRenameTargetParams>(
            LspMethodNames.ReqnrollSelectRenameTarget,
            (request, ct) => resolver!.Get<StepRenameHandler>().HandleSelectRenameTargetAsync(request, ct));
    }

    /// <summary>
    /// Times a manual-route handler invocation through the Layer 4
    /// <see cref="IOperationDurationRecorder"/>, resolved from the running server's services.
    /// </summary>
    private static async Task<T> MeasuredAsync<T>(
        LspHandlerResolver resolver, string operation, DocumentUri? uri, Func<Task<T>> body)
    {
        using var _ = resolver.Get<IOperationDurationRecorder>().Measure(operation, uri);
        return await body().ConfigureAwait(false);
    }

    /// <summary>
    /// Strongly-typed resolver for LSP handlers, eliminating nullable IServiceProvider captures.
    /// </summary>
    private sealed class LspHandlerResolver
    {
        private readonly IServiceProvider _serviceProvider;

        public LspHandlerResolver(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public T Get<T>() where T : notnull => _serviceProvider.GetRequiredService<T>();
    }
}
