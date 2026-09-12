using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Handles <c>textDocument/prepareRename</c>, <c>textDocument/rename</c>, and
/// <c>reqnroll/selectRenameTarget</c> for the Step Rename refactoring feature.
/// <c>reqnroll/renameTargets</c> is handled separately by <see cref="RenameTargetsHandler"/>.
/// </summary>
public sealed class RenameHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly IDocumentBufferService        _documentBuffer;
    private readonly RenameSessionManager          _sessionManager;
    private readonly ICSharpFileTextCache          _csharpFileTextCache;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly ILanguageServerFacade         _languageServer;
    private readonly ClientIdeContext              _clientIdeContext;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;
    private readonly CSharpAttributeLiteralResolver _attributeLiteralResolver;
    private readonly RenameBindingResolver         _bindingResolver;
    private readonly NewNameReconciler             _nameReconciler;
    private readonly RenamePostApplyCoordinator    _postApplyCoordinator;
    private readonly IFileSystemForIDE             _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="RenameHandler"/> class.</summary>
    public RenameHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        IDocumentBufferService        documentBuffer,
        ICSharpFileTextCache          csharpFileTextCache,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        ILanguageServerFacade         languageServer,
        ClientIdeContext              clientIdeContext,
        IFileSystemForIDE             fileSystem,
        ILspTelemetryService?         telemetryService = null,
        IOperationDurationRecorder?   recorder = null,
        ICSharpSyntaxTreeCache?       syntaxTreeCache = null)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _registryLookup  = registryLookup;
        _logger          = logger;
        _documentBuffer  = documentBuffer;
        _sessionManager  = new RenameSessionManager();
        _csharpFileTextCache = csharpFileTextCache;
        _csharpDiscoveryService = csharpDiscoveryService;
        _languageServer  = languageServer;
        _clientIdeContext = clientIdeContext;
        _fileSystem      = fileSystem;
        _telemetryService = telemetryService;
        _recorder        = recorder ?? NullOperationDurationRecorder.Instance;
        _attributeLiteralResolver = new CSharpAttributeLiteralResolver(csharpFileTextCache, documentBuffer, logger, fileSystem, syntaxTreeCache);
        _bindingResolver = new RenameBindingResolver(matchService, scopeManager, _sessionManager, logger);
        _nameReconciler  = new NewNameReconciler(logger);
        _postApplyCoordinator = new RenamePostApplyCoordinator(
            languageServer, clientIdeContext, matchService, documentBuffer,
            csharpDiscoveryService, csharpFileTextCache, logger, _recorder);
    }

    // ── textDocument/prepareRename ──────────────────────────────────────────────

    /// <summary>
    /// Validates that the cursor is on a renameable binding. Returns the range of the
    /// renameable text (attribute string or step text) — for <c>.feature</c> files, paired
    /// with a <see cref="PlaceholderRange.Placeholder"/> carrying the binding's abstract
    /// expression (e.g. <c>"the second number is {int}"</c>) instead of the concrete step
    /// text — or <c>null</c> if rename is not available at this position.
    /// </summary>
    /// <remarks>
    /// Seeding the rename box with the abstract expression, rather than the concrete text
    /// literally in the buffer, is deliberate (issue #33 follow-up): a spec-compliant client
    /// has no buffer text to anchor a partial in-place edit against when the placeholder
    /// text doesn't appear in the document, so it can only submit the box's full edited
    /// content as <c>newName</c> — turning an inherently ambiguous "did the user edit the
    /// wording, the parameter value, or an arbitrary fragment?" problem into an unambiguous
    /// one: <c>newName</c> is always the complete new abstract expression.
    /// </remarks>
    public async Task<RangeOrPlaceholderRange?> HandlePrepareRenameAsync(
        PrepareRenameParams request,
        CancellationToken   cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();

        // Performance Verification (Layer 4): time the prepareRename cursor-validation round-trip.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentPrepareRename, uri);

        if (string.IsNullOrEmpty(path))
            return null;

        // Rule 1: validate cursor position (file type)
        var posError = StepRenameValidator.ValidateCursorPosition((Uri)uri);
        if (posError != null)
        {
            _logger.LogVerbose($"RenameHandler: prepareRename — {posError.Message}");
            return null;
        }

        // Rule 7: validate project state via the registry lookup
        var registry = _registryLookup.GetRegistryForUri(uri);
        bool isInitialized = registry != ProjectBindingRegistry.Invalid;
        bool hasFeatureFiles = false;

        if (isInitialized)
        {
            var project = _scopeManager.GetProjectForUri(uri);
            hasFeatureFiles = project != null &&
                (_scopeManager.GetIndexedFeatureFiles(project).Count > 0
                 || _scopeManager.ResolveOwners(uri).Count > 0);
        }

        var projError = StepRenameValidator.ValidateProjectState(isInitialized, hasFeatureFiles);
        if (projError != null)
        {
            _logger.LogVerbose($"RenameHandler: prepareRename — {projError.Message}");
            return null;
        }

        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await PrepareRenameFromCSharpAsync(uri, path, request.Position, registry);

        if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            return await PrepareRenameFromFeatureAsync(uri, path, request.Position);

        return null;
    }

    /// <summary>
    /// The <c>.cs</c> branch of <see cref="HandlePrepareRenameAsync"/>: checks whether the cursor
    /// resolves to a single binding whose expression is a renameable string literal, and returns
    /// the range of that literal's inner text (quotes excluded).
    /// </summary>
    private async Task<RangeOrPlaceholderRange?> PrepareRenameFromCSharpAsync(
        DocumentUri uri, string path, Position position, ProjectBindingRegistry registry)
    {
        var line   = position.Line + 1;
        var column = position.Character + 1;
        var bindingLocation = new SourceLocation(path, line, column);

        if (registry == ProjectBindingRegistry.Invalid)
            return null;

        var binding = registry.FindBindingAtLocation(bindingLocation);
        if (binding == null)
            return null;

        // Rule 2: validate expression is a string literal
        var exprError = StepRenameValidator.ValidateExpressionIsStringLiteral(binding.Expression);
        if (exprError != null)
        {
            _logger.LogVerbose($"RenameHandler: prepareRename — {exprError.Message}");
            return null;
        }

        // Return the range of the string literal's INNER text only, excluding the
        // surrounding quote characters. Returning the whole line/token (quotes included)
        // seeds the client's rename box with the quotes; if the user leaves them untouched
        // (a natural interaction — they look like part of the placeholder), `newName`
        // arrives already quoted, and BuildCSharpEdit's unconditional `"` + text + `"`
        // wrapping then doubles them, producing a stray trailing quote (issue #55).
        var literal = await _attributeLiteralResolver.FindAttributeLiteralAsync(uri, binding);
        if (literal == null)
        {
            _logger.LogVerbose("RenameHandler: prepareRename — could not resolve attribute literal for binding");
            return null;
        }

        return CSharpAttributeLiteralResolver.GetLiteralInnerRange(literal);
    }

    /// <summary>
    /// The <c>.feature</c> branch of <see cref="HandlePrepareRenameAsync"/>: only offers rename
    /// when the cursor is on a step that is actually defined in the match cache. Returning
    /// <see langword="null"/> tells VS Code "rename not available at this position" — same as
    /// <see cref="PrepareRenameFromCSharpAsync"/> for a C# cursor not on a binding attribute —
    /// which suppresses the rename dialog cleanly. Without this check, prepareRename would
    /// succeed for undefined steps, and the subsequent textDocument/rename would fail with
    /// "Internal Error".
    /// </summary>
    private async Task<RangeOrPlaceholderRange?> PrepareRenameFromFeatureAsync(
        DocumentUri uri, string path, Position position)
    {
        var featureBindings = _bindingResolver.FindBindingsAtFeatureStep(uri, path, position, out var stepRange);
        if (featureBindings.Count == 0)
        {
            _logger.LogVerbose("RenameHandler: prepareRename — no defined binding at feature step position");
            return null;
        }

        if (stepRange == null)
        {
            // Should not happen alongside a non-empty featureBindings, but refuse rather
            // than fall back to a whole-line range: that used to seed the dialog with the
            // keyword/indentation, which then got duplicated when the resulting edit was
            // applied at the step-text-only range HandleRenameAsync actually replaces.
            _logger.LogVerbose("RenameHandler: prepareRename — matched a binding but could not resolve the step's text range");
            return null;
        }

        // When ambiguous (2+ candidate bindings), a plain F2 rename would fall back to the
        // first candidate anyway (see HandleRenameAsync's position-based fallback) — pick
        // the same one here so the placeholder shown matches what would actually be renamed.
        var matchedBinding = featureBindings[0];
        var sourceLiteral  = await _attributeLiteralResolver.FindAttributeLiteralAsync(uri, matchedBinding);
        // Falls back to DisplayExpression, not matchedBinding.Expression's raw auto-generated
        // regex, when no literal can be found (issue #344). DisplayExpression is itself null
        // for a method-name-style binding — there is no attribute text to rename at all — so
        // refuse rename entirely rather than seed the dialog with a null/empty placeholder,
        // mirroring the .cs-cursor branch's "no literal found" bail-out above.
        var sourceExpression = sourceLiteral?.Token.ValueText ?? matchedBinding.DisplayExpression;
        if (sourceExpression == null)
        {
            _logger.LogVerbose("RenameHandler: prepareRename — matched binding has no renameable expression (method-name-style)");
            return null;
        }

        // Known cosmetic quirk (confirmed live in VS and VS Code, issue #33 follow-up): when
        // a user pre-selects a sub-span of the concrete step text before invoking F2 (e.g.
        // "added" in "the two numbers are added"), the client computes that selection's
        // offset relative to Range.Start and reapplies the same numeric offset into
        // Placeholder to decide what to pre-highlight in the rename box. Placeholder is a
        // different string than the concrete text whenever a parameter's rendered width
        // differs from its abstract token (here "are" → "{Verb}", +3 chars), so everything
        // after the parameter shifts and the pre-highlighted substring lands a few characters
        // off from what the user actually selected (e.g. "b} summed" instead of "summed").
        // This is inherent to the offset math being reused across two different-length
        // strings — the LSP PrepareRenameResult protocol has no field to specify which
        // sub-span of Placeholder to highlight independently of Range — and is harmless: the
        // box's full content is still correct, and whatever the user submits becomes newName
        // in full regardless of what was pre-highlighted.
        return new PlaceholderRange
        {
            Range       = stepRange,
            Placeholder = sourceExpression
        };
    }

    // ── textDocument/rename ────────────────────────────────────────────────────

    /// <summary>
    /// Executes the rename. Validates the new name, resolves all feature step locations,
    /// resolves the C# attribute string range, and returns a WorkspaceEdit covering all files.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="HandlePrepareRenameAsync"/> — where a <see langword="null"/> result is
    /// the LSP-spec-sanctioned way to say "not renameable here" and silently suppresses the
    /// client's rename UI — every failure path here throws an <see cref="RpcErrorException"/>
    /// (issue #650) instead of returning <see langword="null"/>. By the time this method runs,
    /// the client has already shown the user a rename box (seeded by a successful
    /// <c>prepareRename</c>) and collected a new name, so a failure at this point is a genuine
    /// error the user should see explained, not a silent no-op — a bare <see langword="null"/>
    /// here previously surfaced as an unqualified "Rename failed" with no reason given.
    /// </remarks>
    public async Task<WorkspaceEdit?> HandleRenameAsync(
        RenameParams       request,
        CancellationToken   cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();
        var newName = request.NewName;

        // Performance Verification (Layer 4): time the full rename — the highest-blast-radius,
        // most complex operation in the server (workspace-wide applyEdit).
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentRename, uri);

        if (string.IsNullOrEmpty(path))
            throw RenameFailedError("No step definition found at this position", "position");
        if (string.IsNullOrEmpty(newName))
            throw RenameFailedError("The new step text cannot be empty", "rename");

        _logger.LogVerbose($"RenameHandler: rename at {path}, newName='{newName}'");

        // ── 1. Resolve binding ─────────────────────────────────────────────────

        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
        {
            _logger.LogVerbose("RenameHandler: registry is invalid");
            throw RenameFailedError("The project is not initialized yet", "project");
        }

        // Resolves a pending reqnroll/selectRenameTarget session first (multi-attribute picker
        // flow), then falls back to feature-match-cache or registry position lookup — see
        // RenameBindingResolver.ResolveBindingForRename for the full precedence order.
        var binding = _bindingResolver.ResolveBindingForRename(uri, path, request.Position, registry);
        if (binding == null)
            throw RenameFailedError("No step definition found at this position", "position");

        var bindingLocation = ResolveBindingLocation(path, binding, line, column);
        var expression = binding.Expression ?? string.Empty;

        // ── 2. Resolve feature step locations ──────────────────────────────────
        var owners = _scopeManager.ResolveOwners(uri);
        var projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker)).ToArray()
            : null;

        var usages = _matchService.FindUsages(bindingLocation, projectFilter);

        // Resolve the live source expression once (preserves the original parameter syntax).
        // For a .cs-invoked rename this is the attribute string literal; otherwise it falls back
        // to the registry expression. It anchors both the feature edits (static-segment
        // substitution) and the C# attribute edit.
        var sourceLiteral = await _attributeLiteralResolver.FindAttributeLiteralAsync(uri, binding);
        var sourceExpression = sourceLiteral?.Token.ValueText ?? expression;

        // Reconciles concrete step text (VS Code's native F2) against the binding's abstract
        // expression (VS's "Rename Step" command) — see NewNameReconciler.Reconcile for the full
        // rationale. Null means the edited text couldn't be reconciled with the binding's
        // parameter positions; the rename is rejected.
        var effectiveNewName = _nameReconciler.Reconcile(
            path, uri, request.Position, usages, sourceExpression, newName, ReadStepText);
        if (effectiveNewName == null)
        {
            throw RenameFailedError(
                "Could not match the new step text to this step's parameters — only the wording can change, not the parameter values",
                "rename");
        }

        // ── 3. Validate new name ───────────────────────────────────────────────
        var nameError = StepRenameValidator.ValidateNewName(expression, effectiveNewName);
        if (nameError != null)
        {
            _logger.LogVerbose($"RenameHandler: validation failed — {nameError.Message}");
            throw RenameFailedError(nameError.Message, nameError.Scope);
        }

        var supportsChangeAnnotations = ClientSupportsChangeAnnotations();

        // A rename that touches more than one .feature file crosses file boundaries the user may
        // not have anticipated from a single step's rename prompt — ask the client to confirm
        // before applying, if it renders that confirmation (see WorkspaceEditBuilder's shape
        // negotiation; unsupported clients never see this flag).
        var featureFileCount = usages.Select(u => u.FeatureDocumentId).Distinct().Count();
        var builder = CreateEditBuilder(supportsChangeAnnotations, effectiveNewName, featureFileCount);

        // ── 4. Build .feature file edits ───────────────────────────────────────
        AddFeatureFileEdits(builder, usages, effectiveNewName, sourceExpression, binding);

        // ── 5. Build .cs file edit ────────────────────────────────────────────
        var (csFileUri, newCsText) = await AddCSharpFileEditAsync(
            builder, uri, path, binding, sourceLiteral, effectiveNewName, cancellationToken);

        if (builder.IsEmpty)
        {
            throw RenameFailedError(
                "The rename produced no changes — the step definition could not be located in source",
                "rename");
        }

        var workspaceEdit = builder.Build();

        // Nothing touches a server-side cache here (issue #671, R3). The registry/match-cache
        // updates this edit implies are staged, and committed only once the client confirms it
        // actually applied the edit — via reqnroll/renameApplied, or for VS via the Applied flag
        // on the post-response workspace/applyEdit push. Committing them inline would desync
        // server state from reality whenever the client declines to apply: the registry would
        // claim the rename succeeded while the source still has the old text (issue #670).
        _postApplyCoordinator.StagePendingCommit(uri, builder, csFileUri, newCsText);

        // Returns immediately for every client; for VS it queues the workspace/applyEdit push to
        // run *after* this response is sent, which is what stops the edit's own didOpen from
        // cancelling this very request with ContentModified (issue #654). See its remarks.
        _postApplyCoordinator.SchedulePostResponseApply(uri, builder);

        // Telemetry
        _telemetryService?.SendEvent(TelemetryEvents.RenameStepCommandExecuted, new()
        {
            ["Erroneous"] = false,
            ["ChangeAnnotationsUsed"] = supportsChangeAnnotations,
            ["EditedFileCount"] = builder.TouchedUris.Count,
        });

        return workspaceEdit;
    }

    /// <summary>
    /// Builds the <see cref="RpcErrorException"/> a <c>textDocument/rename</c> failure throws
    /// (issue #650) so it reaches the client as a proper LSP <c>ResponseError</c> — code
    /// <see cref="ErrorCodes.RequestFailed"/> ("the request was syntactically valid but could
    /// not complete"), the human-readable reason as the error message, and
    /// <paramref name="scope"/> (matching <see cref="ValidationError.Scope"/> where the failure
    /// came from <see cref="StepRenameValidator"/>) as the error's <c>data</c> payload.
    /// </summary>
    private static RpcErrorException RenameFailedError(string message, string? scope = null) =>
        // RpcErrorException's `error` parameter isn't nullable-annotated (it predates this
        // codebase's NRT adoption), but a null `data` is handled fine at the wire level —
        // ErrorMessage.Data is [JsonProperty(NullValueHandling = NullValueHandling.Ignore)].
        new(ErrorCodes.RequestFailed, scope!, message);

    /// <summary>
    /// Resolves the source location to search for feature-step usages of <paramref name="binding"/>:
    /// its own C# source location when the rename originated from a <c>.feature</c> file (the
    /// request path isn't the <c>.cs</c> file in that case), otherwise the cursor position in the
    /// requested <c>.cs</c> file.
    /// </summary>
    private SourceLocation ResolveBindingLocation(
        string path, ProjectStepDefinitionBinding binding, int line, int column)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            binding.Implementation?.SourceLocation?.SourceFile != null)
        {
            var bindingLocation = new SourceLocation(
                binding.Implementation.SourceLocation.SourceFile,
                binding.Implementation.SourceLocation.SourceFileLine,
                binding.Implementation.SourceLocation.SourceFileColumn);
            _logger.LogVerbose($"RenameHandler: using binding source location for FindUsages: {bindingLocation}");
            return bindingLocation;
        }

        return new SourceLocation(path, line, column);
    }

    /// <summary>
    /// Whether the connected client supports grouped, labelled rename previews via
    /// <c>WorkspaceEdit.ChangeAnnotations</c> (issue #70): both <c>documentChanges</c> and
    /// <c>changeAnnotationSupport</c> must be advertised. Everyone else (VS, per Phase 0's
    /// capability survey — see docs/Rename-ChangeAnnotations-Implementation-Plan.md) gets the
    /// legacy <c>Changes</c> shape, byte-identical to before this feature existed.
    /// </summary>
    /// <summary>
    /// The LSP document version an edit for <paramref name="uri"/> is computed against, or
    /// <see langword="null"/> when the client has not opened that document (issue #671, R2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what <c>OptionalVersionedTextDocumentIdentifier</c> exists for — per the spec, "to
    /// allow clients to check the text document version before an edit is applied." Every edit
    /// previously went out with <c>version: null</c>, which opted out of that check entirely and
    /// left each client inventing its own ad-hoc staleness detection instead (issue #671).
    /// </para>
    /// <para>
    /// <see langword="null"/> for a document with no buffer is the spec's own meaning, not a
    /// fallback: "if the file is not open in the editor ... the server can send null to indicate
    /// that the version is known and the content on disk is the master." A rename routinely edits
    /// closed <c>.feature</c> files and a <c>.cs</c> file the client may never have opened against
    /// this server at all.
    /// </para>
    /// <para>
    /// The buffer's version is trustworthy here because <c>textDocument/rename</c> is dispatched on
    /// the Serial lane (R8): any <c>didChange</c> the client has already sent is processed before
    /// this runs, so the version recorded here is the one the edit's offsets were computed against.
    /// </para>
    /// </remarks>
    private int? ResolveDocumentVersion(DocumentUri uri)
        => _documentBuffer.TryGet(uri, out var buffer) ? buffer?.Version : null;

    private bool ClientSupportsChangeAnnotations()
    {
        var workspaceEditCapability = _languageServer.ClientSettings?.Capabilities?.Workspace?.WorkspaceEdit;
        return workspaceEditCapability is not null &&
            workspaceEditCapability.Value.IsSupported &&
            workspaceEditCapability.Value.Value?.DocumentChanges == true &&
            workspaceEditCapability.Value.Value?.ChangeAnnotationSupport is not null;
    }

    private WorkspaceEditBuilder CreateEditBuilder(
        bool supportsChangeAnnotations, string effectiveNewName, int featureFileCount)
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations, ResolveDocumentVersion);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature,
            new ChangeAnnotation
            {
                Label = $"Rename step usages → \"{effectiveNewName}\"",
                NeedsConfirmation = featureFileCount > 1
            });
        builder.DeclareAnnotation(RenameChangeAnnotations.Binding,
            new ChangeAnnotation { Label = "Update step-definition attribute" });
        return builder;
    }

    private void AddFeatureFileEdits(
        WorkspaceEditBuilder builder, IReadOnlyList<StepBindingMatch> usages,
        string effectiveNewName, string sourceExpression, ProjectStepDefinitionBinding binding)
    {
        foreach (var usage in usages)
        {
            var featureUri = DocumentUri.Parse(usage.FeatureDocumentId);

            // Read the feature step text to preserve parameter values / placeholders
            string? stepText = null;
            if (usage.Range != null)
            {
                var stepRange = usage.Range.ToLspRange();
                stepText = ReadStepText(featureUri, stepRange);
            }

            var featureNewText = FeatureStepTextBuilder.Build(effectiveNewName, sourceExpression, binding.Regex, stepText);
            builder.Add(featureUri, usage.Range!.ToLspRange(), featureNewText, RenameChangeAnnotations.Feature);
        }
    }

    /// <summary>
    /// Builds and adds the C# attribute edit to <paramref name="builder"/>, when
    /// <paramref name="sourceLiteral"/> was resolved. Returns the edited file's URI and its full
    /// post-edit text (computed directly from the same Roslyn span the edit used, so it's exact
    /// regardless of whether the <c>.cs</c> file is open or closed in the editor) — both needed by
    /// the caller to refresh server-side caches after the edit is applied.
    /// </summary>
    private async Task<(DocumentUri? CsFileUri, string? NewCsText)> AddCSharpFileEditAsync(
        WorkspaceEditBuilder builder, DocumentUri uri, string path, ProjectStepDefinitionBinding binding,
        Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax? sourceLiteral, string effectiveNewName,
        CancellationToken cancellationToken)
    {
        if (sourceLiteral == null)
            return (null, null);

        var csEdit = _attributeLiteralResolver.BuildEdit(sourceLiteral, effectiveNewName);
        if (csEdit == null)
            return (null, null);

        // An unresolved source location must not become an edit target. Unlike a dead navigation
        // target, which merely does nothing, a workspace/applyEdit against a file that does not
        // exist can apply the feature-file half of the rename while silently dropping the attribute
        // half, leaving the binding broken (issue #540 F7).
        var isCsDocument = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if (!isCsDocument && binding.Implementation?.SourceLocation?.IsResolved == false)
        {
            _logger.LogWarning(
                $"RenameHandler: skipping the C# side of the rename for '{binding.Implementation.Method}' — " +
                $"the compiled assembly records it at '{binding.Implementation.SourceLocation.RecordedSourceFile}', " +
                "which does not exist on this machine. Rebuild the project locally and rename again.");
            return (null, null);
        }

        var csFileUri = isCsDocument
            ? uri
            : DocumentUri.FromFileSystemPath(binding.Implementation!.SourceLocation!.SourceFile);
        builder.Add(csFileUri, csEdit.Range, csEdit.NewText, RenameChangeAnnotations.Binding);

        var sourceText = await sourceLiteral.SyntaxTree!.GetTextAsync(cancellationToken);
        var newCsText = sourceText
            .WithChanges(new Microsoft.CodeAnalysis.Text.TextChange(sourceLiteral.Token.Span, csEdit.NewText))
            .ToString();

        return (csFileUri, newCsText);
    }

    // ── Custom request handlers ─────────────────────────────────────────────────

    /// <summary>
    /// Handles <c>reqnroll/selectRenameTarget</c> — stores the selected attribute
    /// for the next <c>textDocument/rename</c> call.
    /// </summary>
    public Task HandleSelectRenameTargetAsync(
        SelectRenameTargetParams request,
        CancellationToken        cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollSelectRenameTarget, request.Uri);
        _sessionManager.SetSession(request.Uri.ToString(), request.Version, request.AttributeIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles <c>reqnroll/renameApplied</c> — the client reporting whether it applied the
    /// <see cref="WorkspaceEdit"/> returned from the preceding <c>textDocument/rename</c>, which
    /// commits or drops the cache updates staged for it (issue #671, R3).
    /// </summary>
    /// <remarks>
    /// Sent by the clients that apply the edit themselves (Rider, VS Code). Visual Studio never
    /// sends it: the server pushes the edit there via <c>workspace/applyEdit</c> and confirms from
    /// that request's own <c>Applied</c> flag instead — see
    /// <see cref="RenamePostApplyCoordinator.SchedulePostResponseApply"/>.
    /// </remarks>
    public Task HandleRenameAppliedAsync(
        RenameAppliedParams request,
        CancellationToken   cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollRenameApplied, request.Uri);
        _logger.LogVerbose($"RenameHandler: client reported renameApplied={request.Applied} for '{request.Uri}'");
        return _postApplyCoordinator.CompletePendingCommitAsync(request.Uri, request.Applied, cancellationToken);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // ── Feature step text parameter preservation ─────────────────────────────

    /// <summary>
    /// Reads the step text from a feature file at the given range, using the
    /// document buffer if available (open file) or reading from disk.
    /// </summary>
    private string? ReadStepText(DocumentUri featureUri, LspRange range)
    {
        string? fileText = null;
        if (_documentBuffer.TryGet(featureUri, out var buffer) && buffer?.Text != null)
            fileText = buffer.Text;
        else
        {
            var path = featureUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path) && _fileSystem.File.Exists(path))
                fileText = _fileSystem.File.ReadAllText(path);
        }

        if (fileText == null)
            return null;

        var lines = fileText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (range.Start.Line < 0 || range.Start.Line >= lines.Length)
            return null;

        var line = lines[range.Start.Line];
        var start = Math.Min(range.Start.Character, line.Length);
        var end   = Math.Min(range.End.Character, line.Length);
        return start < end ? line.Substring(start, end - start) : null;
    }
}
