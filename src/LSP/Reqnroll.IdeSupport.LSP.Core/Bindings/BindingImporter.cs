#nullable disable
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TagExpressions;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>Initializes a new instance of the <see cref="BindingImporter"/> class.</summary>
    public BindingImporter(Dictionary<string, string> sourceFiles, Dictionary<string, string> typeNames,
        IIdeSupportLogger logger)
    {
        _sourceFiles = sourceFiles;
        _typeNames = typeNames;
        _logger = logger;
    }

    /// <summary>Parses a C# source file into a syntax tree root, for use with the
    /// <see cref="TryGetAttributeSourceLine(SyntaxNode,string,ScenarioBlock)"/> overload. Callers that
    /// process multiple step definitions from the same file should parse once and reuse the root,
    /// rather than calling <see cref="TryGetAttributeSourceLine(string,string,ScenarioBlock)"/>
    /// (which parses the file itself) per step definition.
    /// Returns null when the file cannot be read or parsed.</summary>
    public static SyntaxNode TryParseSourceFile(string sourceFilePath)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
                return null;

            var sourceText = File.ReadAllText(sourceFilePath);
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
    public static int? TryGetAttributeSourceLine(string sourceFilePath, string methodName, ScenarioBlock scenarioBlock)
    {
        var root = TryParseSourceFile(sourceFilePath);
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
        // (e.g. a CI runner, or a plugin built elsewhere), which may not exist on this machine —
        // most commonly for reflection-discovered bindings contributed by an external/dynamically
        // loaded Reqnroll plugin assembly. Treat a missing file the same as a missing location.
        return File.Exists(sourceRef) ? sourceRef : null;
    }

    /// <summary>Converts a wire-format step definition DTO into a <see cref="ProjectStepDefinitionBinding"/>, or null if it's invalid.</summary>
    public ProjectStepDefinitionBinding ImportStepDefinition(StepDefinition stepDefinition,
        int? attributeSourceLine = null)
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
    public ProjectHookBinding ImportHook(Hook hook)
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

        return new SourceLocation(sourceFile, line, column, endLineOrNull, endColumnOrNull);
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
