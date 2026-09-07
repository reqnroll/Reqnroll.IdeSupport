using Microsoft.CodeAnalysis;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Orchestrates one binding-discovery run against a project scope.
/// Invokes the appropriate <see cref="OutProcReqnrollConnector"/> (generic or custom),
/// converts the <see cref="DiscoveryResult"/> into a <see cref="ProjectBindingRegistry"/>
/// via <see cref="BindingImporter"/>, and guards with an assembly-hash check to suppress
/// no-op re-runs.
/// </summary>
/// <remarks>
/// This service is stateless and synchronous; callers run it on a background thread
/// (typically via <see cref="ConnectorBindingRegistryProvider"/>).  Connector construction
/// is delegated to an <see cref="IOutProcConnectorFactory"/> so the selection of generic vs
/// custom connector lives in one place and this orchestrator can be tested with a fake.
/// </remarks>
public sealed class ConnectorDiscoveryService : IConnectorDiscoveryService
{
    private readonly IIdeSupportLogger _logger;
    private readonly IOutProcConnectorFactory _connectorFactory;
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="ConnectorDiscoveryService"/> class.</summary>
    public ConnectorDiscoveryService(IIdeSupportLogger logger, IOutProcConnectorFactory connectorFactory,
        IFileSystemForIDE fileSystem)
    {
        _logger = logger;
        _connectorFactory = connectorFactory;
        _fileSystem = fileSystem;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs discovery for <paramref name="scope"/>.
    /// </summary>
    /// <returns>
    /// A new <see cref="ProjectBindingRegistry"/> and its content hash when discovery
    /// succeeds.  Returns (<paramref name="lastGood"/>, <paramref name="lastHash"/>) unchanged
    /// when the assembly is missing, unchanged, or the connector fails.
    /// </returns>
    public (ProjectBindingRegistry Registry, string Hash) RunDiscovery(
        IProjectScope scope,
        ProjectBindingRegistry lastGood,
        string lastHash,
        CancellationToken ct)
    {
        var assemblyPath = scope.OutputAssemblyPath;

        if (string.IsNullOrEmpty(assemblyPath))
        {
            _logger.LogVerbose($"[{scope.ProjectName}] OutputAssemblyPath not set; skipping discovery.");
            return (lastGood, lastHash);
        }

        if (!_fileSystem.File.Exists(assemblyPath))
        {
            _logger.LogInfo($"[{scope.ProjectName}] Output assembly not found (project not yet built?): {assemblyPath}");
            return (lastGood, lastHash);
        }

        var currentHash = ComputeHash(_fileSystem, assemblyPath);
        if (currentHash == lastHash)
        {
            _logger.LogVerbose($"[{scope.ProjectName}] Assembly unchanged (hash match); skipping discovery.");
            return (lastGood, lastHash);
        }

        ct.ThrowIfCancellationRequested();

        var connector = _connectorFactory.Create(scope);
        var configFilePath = FindConfigFilePath(_fileSystem, scope);

        _logger.LogInfo($"[{scope.ProjectName}] Starting binding discovery: {Path.GetFileName(assemblyPath)}");

        DiscoveryResult result;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = connector.RunDiscovery(assemblyPath, configFilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning($"[{scope.ProjectName}] Connector invocation failed after {DurationFormatter.FormatMilliseconds(sw.Elapsed)}: {ex.Message}");
            return (lastGood, lastHash);
        }
        sw.Stop();

        ct.ThrowIfCancellationRequested();

        LogWarningsAndErrors(scope, result);

        if (result.IsFailed)
        {
            _logger.LogWarning($"[{scope.ProjectName}] Discovery failed after {DurationFormatter.FormatMilliseconds(sw.Elapsed)}: {result.ErrorMessage}");
            return (lastGood, lastHash);
        }

        var registry = BuildRegistry(scope, result);
        _logger.LogInfo(
            $"[{scope.ProjectName}] Discovery complete in {DurationFormatter.FormatMilliseconds(sw.Elapsed)}: " +
            $"{registry.StepDefinitions.Length} step definition(s), {registry.Hooks.Length} hook(s).");
        return (registry, currentHash);
    }

    // ── Registry building ─────────────────────────────────────────────────────

    private ProjectBindingRegistry BuildRegistry(IProjectScope scope, DiscoveryResult result)
    {
        // A project-scoped resolver, so a source path recorded on the build machine can be remapped
        // onto this one instead of stored as-is (issue #540). scope.ProjectFolder is the re-rooting
        // target; without it the resolver degrades to a plain existence check.
        var importer = new BindingImporter(result.SourceFiles, result.TypeNames, _logger, _fileSystem,
            new ProjectSourceFileResolver(scope.ProjectFolder, _fileSystem));

        // Parsed once per unique source file (not per step definition) and reused below, since a
        // single binding class typically contributes many step definitions from the same file.
        var parsedFiles = new Dictionary<string, SyntaxNode>();

        var stepDefinitions = (result.StepDefinitions ?? [])
            .Select(sd => {
                // For connector-discovered bindings, backfill the attribute source line and the
                // method identifier's own location from the source file using Roslyn syntax
                // parsing. The attribute line enables exact AST-based matching in
                // FindBindingAtLocation instead of the heuristic line window that was the only
                // option when AttributeSourceLine was null; the method-identifier location
                // replaces the connector's own PDB sequence-point location (which can land a line
                // or more into the method body) with the precise position Roslyn discovery
                // already uses, so CodeLens and other consumers anchor consistently regardless of
                // which discovery path populated the registry (issue #471 follow-up).
                //
                // sd.Method is the connector's wire-format reference --
                // "{DeclaringTypeName}.{MethodName}({ParamTypeNames})", e.g. "Steps.SetFirstNumber(Int32)"
                // -- not a bare method name, so it must be stripped down to the bare identifier before
                // comparing against MethodDeclarationSyntax.Identifier.Text below (issue #484 follow-up:
                // passing sd.Method straight through made both backfills silently miss on every real
                // connector-discovered binding, matching only in unit tests whose fixtures used an
                // already-bare name that never occurs in production).
                var root = TryGetParsedRoot(importer, sd.SourceLocation, sd.Method, parsedFiles, _fileSystem, _logger);
                var scenarioBlock = Enum.TryParse<ScenarioBlock>(sd.Type, out var parsed) ? parsed : ScenarioBlock.Unknown;
                var bareMethodName = BindingImporter.ExtractBareMethodName(sd.Method);
                var attrLine = root == null ? null : BindingImporter.TryGetAttributeSourceLine(root, bareMethodName, scenarioBlock);
                var methodLocation = root == null ? null : BindingImporter.TryGetMethodIdentifierLocation(root, bareMethodName);

                // The one case worth a warning even when a root parsed successfully: no method
                // declaration by this name was found in it, so the precise identifier location the
                // CodeLens anchor and other consumers need never gets backfilled, and
                // ImportStepDefinition silently falls back to the connector's raw PDB-derived
                // location -- which SourceLocationProvider documents as the first *executable
                // statement* in the method body, not the declaration line (issue #484's symptom:
                // "N step usages" rendering below the method instead of above it). Root causes seen
                // in practice: the file changed since the last build, a partial class split across
                // files, or an overload sharing the same method name.
                if (root != null && methodLocation == null)
                {
                    _logger.LogWarning(
                        $"[Connector] No method declaration named '{bareMethodName}' (from wire reference " +
                        $"'{sd.Method}') found while re-parsing its source for method-identifier backfill " +
                        $"(source location '{sd.SourceLocation}'); CodeLens/navigation for this binding will " +
                        "anchor on the connector's raw PDB-derived location instead, which can land inside " +
                        "the method body rather than on its declaration line.");
                }

                return importer.ImportStepDefinition(sd, attrLine, methodLocation);
            })
            .Where(sd => sd is not null)
            .ToList();

        // Hooks get the same method-identifier backfill as step definitions above. They used to get
        // none, which left them on the raw PDB sequence point -- inside the method body rather than
        // on its declaration -- and skipped the source-file resolution the backfill performs on the
        // way (issue #540 F2). Hooks carry no attribute line (ProjectHookBinding does not model one),
        // so only the identifier location is backfilled here.
        var hooks = (result.Hooks ?? [])
            .Select(h => {
                var root = TryGetParsedRoot(importer, h.SourceLocation, h.Method, parsedFiles, _fileSystem, _logger);
                var bareMethodName = BindingImporter.ExtractBareMethodName(h.Method);
                var methodLocation = root == null
                    ? null
                    : BindingImporter.TryGetMethodIdentifierLocation(root, bareMethodName);

                return importer.ImportHook(h, methodLocation);
            })
            .Where(h => h is not null)
            .ToList();

        ReportUnresolvedSourceFiles(scope, importer);

        // Use a stable hash of the output path as the project hash so the registry
        // can participate in the version-monotonicity guard in ProjectBindingRegistry.
        var projectHash = scope.OutputAssemblyPath.GetHashCode();
        return new ProjectBindingRegistry(stepDefinitions!, hooks!, projectHash);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a content-change key combining the assembly path with its last-write time.
    /// Two calls with the same result mean no rebuild has happened.
    /// </summary>
    private static string ComputeHash(IFileSystemForIDE fileSystem, string assemblyPath)
    {
        try
        {
            var lastWrite = fileSystem.File.GetLastWriteTimeUtc(assemblyPath);
            return $"{assemblyPath}@{lastWrite.Ticks}";
        }
        catch
        {
            return assemblyPath;
        }
    }

    /// Resolves and parses a connector-discovered step definition's source file, parsing each
    /// referenced file at most once per <see cref="BuildRegistry"/> call (<paramref name="parsedFiles"/>
    /// caches the syntax root across step definitions sharing a file). Shared by the attribute-line
    /// and method-identifier-location backfills, both of which need the same parsed root. Logs the
    /// two ways this can fail, at different severities: an unresolvable source file is the expected,
    /// benign case for a reflection-discovered binding from an external plugin assembly whose source
    /// isn't available locally (see <see cref="BindingImporter.ResolveSourceFilePath"/>'s remarks),
    /// so that's verbose-only; a resolved file that fails to parse is unexpected and worth a warning
    /// (once per file, not per step definition, since <paramref name="parsedFiles"/> caches the miss too).
    private static SyntaxNode? TryGetParsedRoot(BindingImporter importer, string? rawSourceLocation,
        string? method, Dictionary<string, SyntaxNode> parsedFiles, IFileSystemForIDE fileSystem,
        IIdeSupportLogger logger)
    {
        var sourceFile = importer.ResolveSourceFilePath(rawSourceLocation);
        if (sourceFile == null)
        {
            logger.LogVerbose(
                $"[Connector] No local source file resolved for '{method}' (source location " +
                $"'{rawSourceLocation}'); method-identifier backfill skipped, using the connector's " +
                "raw PDB-derived location.");
            return null;
        }

        if (!parsedFiles.TryGetValue(sourceFile, out var root))
        {
            root = BindingImporter.TryParseSourceFile(sourceFile, fileSystem);
            parsedFiles[sourceFile] = root;

            if (root == null)
                logger.LogWarning(
                    $"[Connector] Failed to parse resolved source file '{sourceFile}' for method-identifier " +
                    $"backfill (binding '{method}'); every binding from this file will fall back to the " +
                    "connector's raw PDB-derived location.");
        }

        return root;
    }

    /// <summary>
    /// Reports, once per discovery run, the source paths this run could not place on this machine.
    /// </summary>
    /// <remarks>
    /// This is the "fail loudly" half of the issue #540 decision. Everything downstream now omits a
    /// navigation target it cannot resolve rather than emitting a dead one, which fixes the wrong
    /// behaviour but is still invisible on its own — a user whose Go To Definition does nothing
    /// needs to be able to find out why. One warning per run naming the count, the example path and
    /// the remedy; the full list at verbose.
    /// </remarks>
    private void ReportUnresolvedSourceFiles(IProjectScope scope, BindingImporter importer)
    {
        var unresolved = importer.UnresolvedSourceFiles;
        if (unresolved.Count == 0)
            return;

        _logger.LogWarning(
            $"[{scope.ProjectName}] {unresolved.Count} binding source file(s) recorded in the compiled " +
            $"assembly do not exist on this machine (e.g. '{unresolved.First()}'), and could not be " +
            $"mapped onto '{scope.ProjectFolder}'. This normally means the assembly was built " +
            "somewhere else — a container, a CI agent, another machine, or an external binding " +
            "package. Binding matching, diagnostics and CodeLens counts are unaffected, but Go To " +
            "Definition, hook navigation and inlay hints have no local target for these bindings and " +
            "will do nothing. Rebuilding the project locally resolves it.");

        foreach (var path in unresolved)
            _logger.LogVerbose($"[{scope.ProjectName}] Unresolved binding source path: '{path}'");
    }

    private static string FindConfigFilePath(IFileSystemForIDE fileSystem, IProjectScope scope)
    {
        // Search standard Reqnroll/SpecFlow config file names relative to the project folder.
        var candidates = new[]
        {
            Path.Combine(scope.ProjectFolder, "reqnroll.json"),
            //Path.Combine(scope.ProjectFolder, "specflow.json"),
            //Path.Combine(scope.ProjectFolder, "app.config")
        };
        return Array.Find(candidates, fileSystem.File.Exists) ?? string.Empty;
    }

    private void LogWarningsAndErrors(IProjectScope scope, DiscoveryResult result)
    {
        if (result.Warnings is not null)
            foreach (var w in result.Warnings)
                _logger.LogWarning($"[{scope.ProjectName}] {w}");

        if (result.GenericBindingErrors is not null)
            foreach (var e in result.GenericBindingErrors)
                _logger.LogWarning($"[{scope.ProjectName}] Binding error: {e}");
    }
}
