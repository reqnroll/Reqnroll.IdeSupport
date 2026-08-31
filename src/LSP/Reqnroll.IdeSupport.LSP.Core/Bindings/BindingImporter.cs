#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TagExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Converts the wire-format <see cref="StepDefinition"/>/<see cref="Hook"/> DTOs received from
/// the connector into <see cref="ProjectStepDefinitionBinding"/>/<see cref="ProjectHookBinding"/>
/// instances, resolving indexed source-file/type-name references and de-duplicating shared
/// <see cref="ProjectBindingImplementation"/> instances by method name.
/// </summary>
public class BindingImporter
{
    private static readonly string[] EmptyParameterTypes = new string[0];
    private readonly Dictionary<string, ProjectBindingImplementation> _implementations = new();

    private readonly IIdeSupportLogger _logger;
    private readonly Dictionary<string, string> _sourceFiles;
    private readonly ReqnrollTagExpressionParser _tagExpressionParser = new();
    private readonly Dictionary<string, string> _typeNames;
    private readonly IFileSystemForIDE _fileSystem;
    private readonly ISourceFileResolver _sourceFileResolver;
    private readonly HashSet<string> _unresolvedSourceFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="BindingImporter"/> class.</summary>
    /// <param name="sourceFileResolver">
    /// Maps the source paths recorded by discovery onto paths that exist here (issue #540). Pass a
    /// <see cref="ProjectSourceFileResolver"/> when the project folder is known, which is what lets
    /// a devcontainer- or CI-built PDB's paths be remapped instead of stored as-is. Defaults to
    /// <see cref="LocalOnlySourceFileResolver"/>: existence-checked, never remapped.
    /// </param>
    public BindingImporter(Dictionary<string, string> sourceFiles, Dictionary<string, string> typeNames,
        IIdeSupportLogger logger, IFileSystemForIDE fileSystem, ISourceFileResolver sourceFileResolver = null)
    {
        _sourceFiles = sourceFiles;
        _typeNames = typeNames;
        _logger = logger;
        _fileSystem = fileSystem;
        _sourceFileResolver = sourceFileResolver ?? new LocalOnlySourceFileResolver(fileSystem);
    }

    /// <summary>
    /// The distinct source paths this importer was given that could not be resolved to a file on
    /// this machine, as recorded by discovery. Empty for a locally built project.
    /// </summary>
    /// <remarks>
    /// Reported once per discovery run rather than once per binding: one devcontainer-built
    /// assembly produces one unresolved path per binding <em>file</em> but dozens of bindings, and
    /// a warning per binding would bury the signal it exists to give.
    /// </remarks>
    public IReadOnlyCollection<string> UnresolvedSourceFiles => _unresolvedSourceFiles;

    /// <summary>Parses a C# source file into a syntax tree root, for use with the
    /// <see cref="TryGetAttributeSourceLine(SyntaxNode,string,ScenarioBlock)"/> overload. Callers that
    /// process multiple step definitions from the same file should parse once and reuse the root,
    /// rather than calling <see cref="TryGetAttributeSourceLine(string,string,ScenarioBlock,IFileSystemForIDE)"/>
    /// (which parses the file itself) per step definition.
    /// Returns null when the file cannot be read or parsed.</summary>
    public static SyntaxNode TryParseSourceFile(string sourceFilePath, IFileSystemForIDE fileSystem)
    {
        try
        {
            if (!fileSystem.File.Exists(sourceFilePath))
                return null;

            var sourceText = fileSystem.File.ReadAllText(sourceFilePath);
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, new CSharpParseOptions(kind: SourceCodeKind.Regular));
            return syntaxTree.GetRoot();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Tries to backfill the `attributeSourceLine` for a connector-discovered step definition
    /// by parsing its source file with Roslyn and looking for the binding attribute above the method.
    /// Returns null when the source file cannot be read or no matching attribute is found.</summary>
    public static int? TryGetAttributeSourceLine(string sourceFilePath, string methodName, ScenarioBlock scenarioBlock,
        IFileSystemForIDE fileSystem)
    {
        var root = TryParseSourceFile(sourceFilePath, fileSystem);
        return root == null ? null : TryGetAttributeSourceLine(root, methodName, scenarioBlock);
    }

    /// <summary>Tries to backfill the `attributeSourceLine` for a connector-discovered step definition
    /// against an already-parsed syntax tree root — see <see cref="TryParseSourceFile"/>. Considers
    /// every method with a matching name (not just the first) and, on each, the binding attribute that
    /// registers for <paramref name="scenarioBlock"/> — resolving the attribute name the same way
    /// <see cref="StepDefinitionFileParser"/> does (namespace-qualification and "Attribute" suffix
    /// stripped), so a method carrying both e.g. [Given] and [When] resolves each block to its own
    /// line instead of collapsing onto whichever attribute is scanned first. <see cref="ScenarioBlock.Unknown"/>
    /// matches any binding attribute, for callers that cannot determine the wire type.
    /// Returns null when no matching attribute is found on any candidate method.</summary>
    public static int? TryGetAttributeSourceLine(SyntaxNode root, string methodName, ScenarioBlock scenarioBlock)
    {
        var candidateMethods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName);

        foreach (var method in candidateMethods)
        {
            foreach (var attributeList in method.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var attributeName = StepDefinitionFileParser.GetAttributeName(attribute);
                    if (!StepDefinitionFileParser.StepDefinitionAttributes.TryGetValue(attributeName, out var blocks))
                        continue;

                    if (scenarioBlock != ScenarioBlock.Unknown && !blocks.Contains(scenarioBlock))
                        continue;

                    return attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the bare method name from a connector wire-format method reference —
    /// <c>"{DeclaringTypeName}.{MethodName}({ParamTypeNames})"</c>, e.g.
    /// <c>"Steps.SetFirstNumber(Int32)"</c> (see <c>DiscoveryResultTransformer.GetMethodReference</c>
    /// on the connector side) — for comparison against a Roslyn
    /// <see cref="MethodDeclarationSyntax"/>'s <c>Identifier.Text</c>, which is never qualified or
    /// parenthesized. <see cref="TryGetAttributeSourceLine(SyntaxNode,string,ScenarioBlock)"/> and
    /// <see cref="TryGetMethodIdentifierLocation"/> compare by name only and never stripped this
    /// themselves, so passing the raw wire-format reference straight through made both backfills
    /// silently miss on every real connector-discovered binding (they matched only in tests, whose
    /// fixtures used an already-bare method name that never occurs in production — issue #484
    /// follow-up).
    /// </summary>
    /// <remarks>
    /// Falls back to the input unchanged when it doesn't look like that shape (no <c>(</c>, or no
    /// <c>.</c> before it) — a wire format that already carries a bare name (existing test fixtures,
    /// or a future format change) round-trips correctly instead of being mangled.
    /// </remarks>
    public static string ExtractBareMethodName(string wireMethodReference)
    {
        if (string.IsNullOrEmpty(wireMethodReference))
            return wireMethodReference;

        var parenIndex = wireMethodReference.IndexOf('(');
        var beforeParen = parenIndex >= 0 ? wireMethodReference.Substring(0, parenIndex) : wireMethodReference;

        var lastDot = beforeParen.LastIndexOf('.');
        return lastDot >= 0 ? beforeParen.Substring(lastDot + 1) : beforeParen;
    }

    /// <summary>
    /// Backfills the exact method-identifier source location for a connector-discovered step
    /// definition — mirrors <see cref="StepDefinitionFileParser"/>'s own AST-based line/column for
    /// Roslyn-discovered bindings (the method identifier's line, not the attribute's, matching
    /// standard "go to definition"/CodeLens-anchor convention). The connector's own wire-format
    /// location is a PDB sequence point, which can land a line or more into the method body rather
    /// than on the declaration itself; this replaces it with the same precise position Roslyn
    /// discovery already uses, once the AST is available for this backfill pass anyway.
    /// Returns null when no method with this name is found (e.g. partial class defined elsewhere,
    /// or the source no longer matches what the connector saw at build time). <paramref name="methodName"/>
    /// must already be a bare method name — see <see cref="ExtractBareMethodName"/> for callers
    /// starting from a connector wire-format method reference.
    /// </summary>
    public static (int Line, int Column)? TryGetMethodIdentifierLocation(SyntaxNode root, string methodName)
    {
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
            return null;

        var pos = method.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        return (pos.Line + 1, pos.Character + 1);
    }

    /// <summary>Resolves the source file path referenced by a connector-discovered binding's raw
    /// wire-format source location, using the same "#index" table lookup as <see cref="ParseSourceLocation"/>.
    /// Returns null when the location is empty or the referenced/literal file does not exist.</summary>
    public string ResolveSourceFilePath(string sourceLocationRaw)
    {
        if (string.IsNullOrWhiteSpace(sourceLocationRaw))
            return null;

        var sourceRef = sourceLocationRaw.Split('|')[0];
        if (sourceRef.StartsWith("#") && _sourceFiles != null &&
            _sourceFiles.TryGetValue(sourceRef.Substring(1), out var resolvedPath))
            sourceRef = resolvedPath;

        // The PDB records the absolute source path from the machine that built the assembly
        // (e.g. a CI runner, a devcontainer, or a plugin built elsewhere), which may not exist on
        // this machine. The resolver gets one chance to map it onto something local; a path it
        // cannot place is treated the same as a missing location.
        return ResolveSourceFile(sourceRef);
    }

    /// <summary>Converts a wire-format step definition DTO into a <see cref="ProjectStepDefinitionBinding"/>, or null if it's invalid.</summary>
    public ProjectStepDefinitionBinding ImportStepDefinition(StepDefinition stepDefinition,
        int? attributeSourceLine = null, (int Line, int Column)? methodIdentifierLocation = null)
    {
        try
        {
            var stepDefinitionType = Enum.TryParse<ScenarioBlock>(stepDefinition.Type, out var parsedHookType)
                ? parsedHookType
                : ScenarioBlock.Unknown;
            var regex = ParseRegex(stepDefinition);
            var sourceLocation = ParseSourceLocation(stepDefinition.SourceLocation);
            var scope = ParseScope(stepDefinition.Scope);
            var parameterTypes = ParseParameterTypes(stepDefinition.ParamTypes);

            if (!_implementations.TryGetValue(stepDefinition.Method, out var implementation))
            {
                // Prefer the AST-backfilled method-identifier location over the connector's own
                // PDB-derived one (see TryGetMethodIdentifierLocation's remarks) when the same
                // backfill pass that resolved it for this method succeeded.
                // WithPosition, not a fresh SourceLocation: rebuilding it through the public
                // constructor would silently re-assert IsResolved, discarding the fact that this
                // binding's file could not be found on this machine (issue #540 F1).
                if (methodIdentifierLocation.HasValue && sourceLocation != null)
                    sourceLocation = sourceLocation.WithPosition(
                        methodIdentifierLocation.Value.Line, methodIdentifierLocation.Value.Column,
                        sourceLocation.SourceFileEndLine, sourceLocation.SourceFileEndColumn);

                implementation =
                    new ProjectBindingImplementation(stepDefinition.Method, parameterTypes, sourceLocation);
                _implementations.Add(stepDefinition.Method, implementation);
            }

            return new ProjectStepDefinitionBinding(stepDefinitionType, regex, scope, implementation,
                stepDefinition.Expression, GetBindingError(stepDefinition.Error, scope, "step definition"),
                attributeSourceLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Invalid step definition binding: {ex.Message}");
            return null;
        }
    }

    /// <summary>Converts a wire-format hook DTO into a <see cref="ProjectHookBinding"/>, or null if it's invalid.</summary>
    /// <param name="hook">The wire-format hook to import.</param>
    /// <param name="methodIdentifierLocation">
    /// The AST-backfilled method-identifier position, when the caller resolved and parsed the hook's
    /// source file — see <see cref="TryGetMethodIdentifierLocation"/>. Hooks used to get no backfill
    /// at all while step definitions did, which left every hook anchored on its raw PDB sequence
    /// point (the first executable statement in the body, not the declaration line) and, because the
    /// backfill is also what forces a source path to be resolved, made hook navigation the first
    /// casualty of a foreign-path build (issue #540 F2).
    /// </param>
    public ProjectHookBinding ImportHook(Hook hook, (int Line, int Column)? methodIdentifierLocation = null)
    {
        try
        {
            var hookType = Enum.TryParse<HookType>(hook.Type, out var parsedHookType)
                ? parsedHookType
                : HookType.Unknown;
            var sourceLocation = ParseSourceLocation(hook.SourceLocation);
            var scope = ParseScope(hook.Scope);

            if (!_implementations.TryGetValue(hook.Method, out var implementation))
            {
                if (methodIdentifierLocation.HasValue && sourceLocation != null)
                    sourceLocation = sourceLocation.WithPosition(
                        methodIdentifierLocation.Value.Line, methodIdentifierLocation.Value.Column,
                        sourceLocation.SourceFileEndLine, sourceLocation.SourceFileEndColumn);

                implementation =
                    new ProjectBindingImplementation(hook.Method, null, sourceLocation);
                _implementations.Add(hook.Method, implementation);
            }

            return new ProjectHookBinding(implementation, scope, hookType, hook.HookOrder, GetBindingError(hook.Error, scope, "hook"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Invalid hook binding: {ex.Message}");
            return null;
        }
    }

    private string GetBindingError(string error, BindingScope scope, string bindingType)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return $"Invalid {bindingType}: {error}";
        if (!string.IsNullOrWhiteSpace(scope?.Error))
            return $"Invalid scope for {bindingType}: {scope.Error}";
        return null;
    }

    private static Regex ParseRegex(StepDefinition stepDefinition) =>
        string.IsNullOrEmpty(stepDefinition.Regex)
            ? null
            : new Regex(stepDefinition.Regex, RegexOptions.CultureInvariant);

    private string[] ParseParameterTypes(string paramTypes)
    {
        if (string.IsNullOrWhiteSpace(paramTypes))
            return EmptyParameterTypes;

        var parts = paramTypes.Split('|');
        return parts.Select(ParseParameterType).ToArray();
    }

    private string ParseParameterType(string paramType)
    {
        paramType = paramType.Trim();

        if (TypeShortcuts.FromShortcut.TryGetValue(paramType, out var shortcutTypeName))
            return shortcutTypeName;

        if (paramType.StartsWith("#") && _typeNames != null)
            if (_typeNames.TryGetValue(paramType.Substring(1), out var typeNameAtIndex))
                paramType = typeNameAtIndex;

        return paramType;
    }

    private SourceLocation ParseSourceLocation(string sourceLocation)
    {
        if (string.IsNullOrWhiteSpace(sourceLocation))
            return null;
        var parts = sourceLocation.Split('|');
        if (parts.Length <= 1 || !int.TryParse(parts[1], out var line))
            line = 1;
        if (parts.Length <= 2 || !int.TryParse(parts[2], out var column))
            column = 1;
        int? endLineOrNull = null;
        if (parts.Length > 3 && int.TryParse(parts[3], out var endLine))
            endLineOrNull = endLine;
        int? endColumnOrNull = null;
        if (parts.Length > 4 && int.TryParse(parts[4], out var endColumn))
            endColumnOrNull = endColumn;

        string sourceFile = parts[0];
        if (sourceFile.StartsWith("#") && _sourceFiles != null)
            if (_sourceFiles.TryGetValue(sourceFile.Substring(1), out var sourceFileAtIndex))
                sourceFile = sourceFileAtIndex;

        // Resolve here, once, rather than at each use site: this is the only place that sees the
        // recorded path before anything downstream can turn it into a navigation target, and the
        // only place with the project context needed to remap it (issue #540 F1). Before this, a
        // foreign path was stored verbatim and every consumer handed it to the IDE unchecked.
        var resolved = ResolveSourceFile(sourceFile);
        return resolved != null
            ? SourceLocation.Resolved(resolved, sourceFile, line, column, endLineOrNull, endColumnOrNull)
            : SourceLocation.Unresolved(sourceFile, line, column, endLineOrNull, endColumnOrNull);
    }

    /// <summary>Resolves one recorded source path, remembering the ones that could not be placed.</summary>
    private string ResolveSourceFile(string recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath))
            return null;

        var resolved = _sourceFileResolver.Resolve(recordedPath);
        if (resolved == null)
            _unresolvedSourceFiles.Add(recordedPath);

        return resolved;
    }

    private BindingScope ParseScope(StepScope bindingScope)
    {
        if (bindingScope == null)
            return null;

        var tagExpression = _tagExpressionParser.Parse(bindingScope.Tag);

        if (tagExpression is InvalidTagExpression ite)
        {
            _logger.LogVerbose($"Invalid tag expression '{bindingScope.Tag}': {ite.Message}");
            return new BindingScope
            {
                FeatureTitle = bindingScope.FeatureTitle,
                ScenarioTitle = bindingScope.ScenarioTitle,
                Tag = null,
                Error = $"Invalid tag expression '{bindingScope.Tag}': {ite.Message}"
            };
        }
        return new BindingScope
        {
            FeatureTitle = bindingScope.FeatureTitle,
            ScenarioTitle = bindingScope.ScenarioTitle,
            Tag = string.IsNullOrWhiteSpace(bindingScope.Tag)
                    ? null
                    : tagExpression,
            Error = bindingScope.Error
        };
    }
}
