using Gherkin.Ast;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using System.IO;

namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// Implements <see cref="IScenarioTestTargetResolver"/> by parsing the project's generated
/// <c>&lt;feature&gt;.feature.cs</c> code-behind with Roslyn rather than predicting Reqnroll's
/// naming rules blind — see docs/Test-Runner-Integration-Design.md §3.
/// </summary>
public sealed class ScenarioTestTargetResolver : IScenarioTestTargetResolver
{
    /// <inheritdoc/>
    public IReadOnlyList<ScenarioTestTarget> Resolve(
        Uri featureUri,
        IReadOnlyCollection<DeveroomTag> tags,
        GherkinRange scenarioRange,
        IReadOnlyCollection<string> projectPackageIds,
        string? projectFolder = null)
    {
        var generatedFilePath = GetGeneratedFilePath(featureUri, projectFolder);
        if (generatedFilePath is null || !File.Exists(generatedFilePath))
            return Array.Empty<ScenarioTestTarget>(); // not built yet — see design doc §3's trade-off table

        if (tags.FirstOrDefault(t => t.Type == DeveroomTagTypes.FeatureBlock)?.Data is not Feature feature)
            return Array.Empty<ScenarioTestTarget>();

        var scenarioTag = FindScenarioTag(tags, scenarioRange);
        var scenarioName = GetScenarioName(scenarioTag?.Data);
        if (scenarioName is null)
            return Array.Empty<ScenarioTestTarget>();

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(generatedFilePath)).GetRoot();
        var expectedClassName = ReqnrollIdentifierNaming.ToIdentifier(feature.Name) + "Feature";
        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == expectedClassName);
        if (classDecl is null)
            return Array.Empty<ScenarioTestTarget>();

        var declaringTypeFullName = GetFullTypeName(classDecl);
        var expectedMethodName = ReqnrollIdentifierNaming.ToIdentifier(scenarioName);
        var selectedRow = FindSelectedExamplesRow(tags, scenarioRange);

        var exactMethod = classDecl.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == expectedMethodName);
        if (exactMethod is not null)
            return ResolveExactMethod(exactMethod, declaringTypeFullName, expectedMethodName,
                scenarioTag!.Data, projectPackageIds, selectedRow);

        var prefix = expectedMethodName + "_";
        var candidateMethods = classDecl.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (candidateMethods.Count == 0)
            return Array.Empty<ScenarioTestTarget>(); // naming-rule mismatch or generator-version drift

        return ResolveIndividualMethods(candidateMethods, declaringTypeFullName, scenarioTag!.Data,
            expectedMethodName, selectedRow);
    }

    // ── Tier 1: locate the generated companion file / class / exact-name method ────────────────

    /// <summary>
    /// Locates the generated <c>&lt;feature&gt;.feature.cs</c> companion. Tries the classic
    /// co-located convention first (<c>Foo.feature</c> -&gt; <c>Foo.feature.cs</c>, next to it), then
    /// falls back to searching under <paramref name="projectFolder"/>'s <c>obj/</c> tree — Reqnroll
    /// 3.3.0 added an MSBuild option (<c>GenerateFeatureFileCodeBehindInProjectDirectory=false</c>)
    /// that relocates code-behind generation under the intermediate output directory instead, to
    /// avoid touching the source tree. The exact obj-relative layout isn't a stable public contract
    /// (it varies with configuration/TFM), so this matches by file name anywhere under <c>obj/</c>
    /// rather than predicting a specific subpath.
    /// </summary>
    private static string? GetGeneratedFilePath(Uri featureUri, string? projectFolder)
    {
        if (!featureUri.IsAbsoluteUri)
            return null;

        string localPath;
        try
        {
            localPath = featureUri.LocalPath;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(localPath))
            return null;

        var coLocatedPath = localPath + ".cs";
        if (File.Exists(coLocatedPath))
            return coLocatedPath;

        return FindInObjFolder(localPath, projectFolder) ?? coLocatedPath;
    }

    private static string? FindInObjFolder(string featurePath, string? projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder))
            return null;

        var objFolder = Path.Combine(projectFolder, "obj");
        if (!Directory.Exists(objFolder))
            return null;

        var expectedFileName = Path.GetFileName(featurePath) + ".cs";
        try
        {
            return Directory.EnumerateFiles(objFolder, expectedFileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetFullTypeName(ClassDeclarationSyntax classDecl)
    {
        var segments = new List<string> { classDecl.Identifier.Text };
        for (var parent = classDecl.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case TypeDeclarationSyntax type:
                    segments.Insert(0, type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    segments.Insert(0, ns.Name.ToString());
                    break;
            }
        }
        return string.Join(".", segments);
    }

    // ── Gherkin-side context resolution ─────────────────────────────────────────────────────────

    private static DeveroomTag? FindScenarioTag(IReadOnlyCollection<DeveroomTag> tags, GherkinRange range) =>
        tags.FirstOrDefault(t => t.Type == DeveroomTagTypes.ScenarioDefinitionBlock && t.Range.IntersectsWith(range));

    private static string? GetScenarioName(object? data) => data switch
    {
        ScenarioOutline so => so.Name,
        Scenario sc => sc.Name,
        _ => null,
    };

    /// <summary>
    /// If <paramref name="range"/> lands within a specific <c>Examples:</c> row rather than the
    /// scenario's own header/steps, returns that block+row — the "resolve at an individual
    /// Examples: row" case from design doc §3.
    /// </summary>
    private static (Examples Examples, TableRow Row)? FindSelectedExamplesRow(
        IReadOnlyCollection<DeveroomTag> tags, GherkinRange range)
    {
        var examplesTag = tags.FirstOrDefault(t =>
            t.Type == DeveroomTagTypes.ExamplesBlock && t.Range.IntersectsWith(range));
        if (examplesTag?.Data is not Examples examples || examples.TableBody is null)
            return null;

        var oneBasedLine = range.StartLinePosition.Line + 1;
        var row = examples.TableBody.FirstOrDefault(r => r.Location.Line == oneBasedLine);
        return row is null ? null : (examples, row);
    }

    private static List<(Examples Examples, TableRow Row)> GetAllExamplesRows(object? scenarioData)
    {
        var result = new List<(Examples, TableRow)>();
        if (scenarioData is ScenarioOutline { Examples: not null } outline)
        {
            foreach (var examples in outline.Examples)
            {
                if (examples.TableBody is null)
                    continue;
                foreach (var row in examples.TableBody)
                    result.Add((examples, row));
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildRowArguments(Examples examples, TableRow row)
    {
        var headers = examples.TableHeader?.Cells?.ToList() ?? new List<TableCell>();
        var cells = row.Cells?.ToList() ?? new List<TableCell>();
        var result = new Dictionary<string, string>();
        for (var i = 0; i < headers.Count && i < cells.Count; i++)
            result[headers[i].Value] = cells[i].Value;
        return result;
    }

    // ── Row-tests mode (exact-name method found) ────────────────────────────────────────────────

    private static IReadOnlyList<ScenarioTestTarget> ResolveExactMethod(
        MethodDeclarationSyntax method, string declaringTypeFullName, string methodName,
        object? scenarioData, IReadOnlyCollection<string> projectPackageIds,
        (Examples Examples, TableRow Row)? selectedRow)
    {
        var framework = TestFrameworkDetection.Detect(projectPackageIds);
        var rowAttributeCount = framework is null
            ? 0
            : CountAttributes(method, RowAttributeTypeNames.ByFramework[framework.Value]);

        if (rowAttributeCount == 0)
            return new[] { new ScenarioTestTarget(declaringTypeFullName, methodName, false, null, null) };

        // Row-tests mode. Correlate against the .feature file's own Examples rows only when the
        // counts line up (ordinary Outline) — never short-circuit to "0 targets" when they don't
        // (e.g. a Reqnroll.ExternalData-style AST-injected scenario with zero visible rows in the
        // .feature file, design doc §2/§7 item 8): still report the row-attribute count, just
        // without RowArguments.
        var featureRows = GetAllExamplesRows(scenarioData);
        var correlateByPosition = featureRows.Count == rowAttributeCount;

        var targets = new List<ScenarioTestTarget>(rowAttributeCount);
        for (var i = 0; i < rowAttributeCount; i++)
        {
            var rowArgs = correlateByPosition ? BuildRowArguments(featureRows[i].Examples, featureRows[i].Row) : null;
            targets.Add(new ScenarioTestTarget(declaringTypeFullName, methodName, true, rowArgs, i));
        }

        if (selectedRow is { } sel && correlateByPosition)
        {
            var index = featureRows.FindIndex(r => ReferenceEquals(r.Row, sel.Row));
            if (index >= 0)
                return new[] { targets[index] };
        }

        return targets;
    }

    private static int CountAttributes(MethodDeclarationSyntax method, string attributeSimpleName)
    {
        var count = 0;
        foreach (var attributeList in method.AttributeLists)
            foreach (var attribute in attributeList.Attributes)
                if (GetAttributeSimpleName(attribute) == attributeSimpleName)
                    count++;
        return count;
    }

    private static string GetAttributeSimpleName(AttributeSyntax attribute)
    {
        var name = attribute.Name;
        while (name is QualifiedNameSyntax qualified)
            name = qualified.Right;
        if (name is AliasQualifiedNameSyntax aliasQualified)
            name = aliasQualified.Name;

        var text = (name as SimpleNameSyntax)?.Identifier.Text ?? name.ToString();
        return text.EndsWith("Attribute", StringComparison.Ordinal) ? text : text + "Attribute";
    }

    // ── Individual-methods mode (allowRowTests = false) ─────────────────────────────────────────

    private static IReadOnlyList<ScenarioTestTarget> ResolveIndividualMethods(
        List<MethodDeclarationSyntax> candidateMethods, string declaringTypeFullName,
        object? scenarioData, string expectedMethodName, (Examples Examples, TableRow Row)? selectedRow)
    {
        var allTargets = candidateMethods
            .Select(m => new ScenarioTestTarget(declaringTypeFullName, m.Identifier.Text, false, null, null))
            .ToList();

        if (selectedRow is not { } sel || scenarioData is not ScenarioOutline outline)
            return allTargets;

        var nameByRow = ComputeIndividualMethodNames(outline, expectedMethodName);
        if (!nameByRow.TryGetValue(sel.Row, out var expectedName))
            return allTargets;

        var match = allTargets.FirstOrDefault(t => t.MethodName == expectedName);
        return match is not null ? new[] { match } : allTargets;
    }

    /// <summary>
    /// Ports design doc §2's individual-methods naming rule:
    /// <c>{scenario.Name.ToIdentifier()}_{exampleSetIdentifier}_{variantName.ToIdentifier()}</c>,
    /// where <c>variantName</c> is the row's first cell value if unique across its own
    /// <c>Examples:</c> block, else <c>"Variant {index}"</c> (0-based within the block); and
    /// <c>exampleSetIdentifier</c> is the block's own name if given, folded out entirely if there is
    /// exactly one unnamed block, else <c>"ExampleSet {n}"</c> (0-based among the unnamed blocks).
    /// </summary>
    private static Dictionary<TableRow, string> ComputeIndividualMethodNames(ScenarioOutline outline, string expectedMethodName)
    {
        var result = new Dictionary<TableRow, string>();
        var blocks = outline.Examples?.ToList() ?? new List<Examples>();
        var unnamedBlockCount = blocks.Count(b => string.IsNullOrWhiteSpace(b.Name));
        var unnamedIndex = 0;

        foreach (var block in blocks)
        {
            string? exampleSetIdentifier;
            if (!string.IsNullOrWhiteSpace(block.Name))
                exampleSetIdentifier = block.Name;
            else if (unnamedBlockCount == 1)
                exampleSetIdentifier = null;
            else
                exampleSetIdentifier = $"ExampleSet {unnamedIndex++}";

            var rows = block.TableBody?.ToList() ?? new List<TableRow>();
            var firstCellValues = rows.Select(r => r.Cells?.Select(c => c.Value).FirstOrDefault()).ToList();
            var firstCellsUnique = firstCellValues.All(v => v is not null)
                && firstCellValues.Distinct().Count() == firstCellValues.Count;

            for (var i = 0; i < rows.Count; i++)
            {
                var variantName = firstCellsUnique ? firstCellValues[i]! : $"Variant {i}";
                var parts = new List<string> { expectedMethodName };
                if (exampleSetIdentifier is not null)
                    parts.Add(ReqnrollIdentifierNaming.ToIdentifier(exampleSetIdentifier));
                parts.Add(ReqnrollIdentifierNaming.ToIdentifier(variantName));
                result[rows[i]] = string.Join("_", parts);
            }
        }

        return result;
    }
}
