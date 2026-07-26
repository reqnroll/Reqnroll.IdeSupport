using Microsoft.CodeAnalysis;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

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
            _logger.LogWarning($"[{scope.ProjectName}] Connector invocation failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return (lastGood, lastHash);
        }
        sw.Stop();

        ct.ThrowIfCancellationRequested();

        LogWarningsAndErrors(scope, result);

        if (result.IsFailed)
        {
            _logger.LogWarning($"[{scope.ProjectName}] Discovery failed after {sw.ElapsedMilliseconds}ms: {result.ErrorMessage}");
            return (lastGood, lastHash);
        }

        var registry = BuildRegistry(scope, result);
        _logger.LogInfo(
            $"[{scope.ProjectName}] Discovery complete in {sw.ElapsedMilliseconds}ms: " +
            $"{registry.StepDefinitions.Length} step definition(s), {registry.Hooks.Length} hook(s).");
        return (registry, currentHash);
    }

    // ── Registry building ─────────────────────────────────────────────────────

    private ProjectBindingRegistry BuildRegistry(IProjectScope scope, DiscoveryResult result)
    {
        var importer = new BindingImporter(result.SourceFiles, result.TypeNames, _logger, _fileSystem);

        // Parsed once per unique source file (not per step definition) and reused below, since a
        // single binding class typically contributes many step definitions from the same file.
        var parsedFiles = new Dictionary<string, SyntaxNode>();

        var stepDefinitions = (result.StepDefinitions ?? [])
            .Select(sd => {
                // For connector-discovered bindings, backfill the attribute source line
                // from the source file using Roslyn syntax parsing. This enables exact
                // AST-based matching in FindBindingAtLocation instead of the heuristic
                // line window that was the only option when AttributeSourceLine was null.
                var attrLine = TryGetAttributeSourceLine(importer, sd, parsedFiles, _fileSystem);
                return importer.ImportStepDefinition(sd, attrLine);
            })
            .Where(sd => sd is not null)
            .ToList();

        var hooks = (result.Hooks ?? [])
            .Select(h => importer.ImportHook(h))
            .Where(h => h is not null)
            .ToList();

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

    /// Backfills the exact attribute source line for a connector-discovered step definition,
    /// parsing each referenced source file at most once per <see cref="BuildRegistry"/> call
    /// (<paramref name="parsedFiles"/> caches the syntax root across step definitions sharing a file).
    private static int? TryGetAttributeSourceLine(BindingImporter importer, StepDefinition sd,
        Dictionary<string, SyntaxNode> parsedFiles, IFileSystemForIDE fileSystem)
    {
        var sourceFile = importer.ResolveSourceFilePath(sd.SourceLocation);
        if (sourceFile == null)
            return null;

        if (!parsedFiles.TryGetValue(sourceFile, out var root))
        {
            root = BindingImporter.TryParseSourceFile(sourceFile, fileSystem);
            parsedFiles[sourceFile] = root;
        }

        if (root == null)
            return null;

        var scenarioBlock = Enum.TryParse<ScenarioBlock>(sd.Type, out var parsed) ? parsed : ScenarioBlock.Unknown;
        return BindingImporter.TryGetAttributeSourceLine(root, sd.Method, scenarioBlock);
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
