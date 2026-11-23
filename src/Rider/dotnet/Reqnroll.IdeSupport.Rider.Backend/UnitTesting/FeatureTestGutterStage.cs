using System;
using System.Collections.Generic;
using System.Reflection;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Properties;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.UnitTestFramework;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Exploration.Daemon;
using JetBrains.ReSharper.UnitTestFramework.Exploration.Daemon.Highlighting;
using JetBrains.ReSharper.UnitTestFramework.Persistence;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using JetBrains.Util.Dotnet.TargetFrameworkIds;
using JetBrains.Util.Logging;
using Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Puts Rider's own unit-test gutter marks on a <c>.feature</c> file: on the <c>Feature</c> line for the generated
/// test class, on each scenario line for its test method, and on each example row of a Scenario Outline for its
/// data row. The marks run and debug through Rider's Unit Tests window and show the last outcome.
/// <para>
/// The tests are the ones Rider discovered in the generated <c>.feature.cs</c> (any framework); they are never
/// reported through discovery, so the code-behind keeps owning them. Lines come from the generated code
/// (<see cref="GeneratedTestLines"/>). The highlighting is Rider's internal <c>UnitTestElementHighlighting</c>,
/// created by reflection, because it carries the status icon, the native Run/Debug menu and the icon refresh after
/// a run. If a Rider release removes or changes it, the stage logs a warning once and adds nothing.
/// </para>
/// </summary>
[DaemonStage(Instantiation.DemandAnyThreadSafe)]
public class FeatureTestGutterStage : IDaemonStage
{
    // Checked against the SDK the module is built with by the backend tests (UnitTestHighlightingContract).
    private const string HighlightingTypeName = UnitTestHighlightingContract.TypeName;

    private static readonly ILogger Log = Logger.GetLogger<FeatureTestGutterStage>();

    private static readonly Lazy<ConstructorInfo> HighlightingConstructor = new Lazy<ConstructorInfo>(FindHighlightingConstructor);

    private readonly IUnitTestElementRepository _repository;
    private readonly FeatureTestGutterRefresher _refresher;

    public FeatureTestGutterStage(IUnitTestElementRepository repository, FeatureTestGutterRefresher refresher)
    {
        _repository = repository;
        _refresher = refresher;
    }

    public IEnumerable<IDaemonStageProcess> CreateProcess(
        IDaemonProcess process, IContextBoundSettingsStore settings, DaemonProcessKind processKind)
    {
        if (processKind != DaemonProcessKind.VISIBLE_DOCUMENT)
            return EmptyList<IDaemonStageProcess>.Instance;
        if (!(process.SourceFile.PrimaryPsiLanguage is FeatureLanguage))
            return EmptyList<IDaemonStageProcess>.Instance;
        if (HighlightingConstructor.Value == null)
            return EmptyList<IDaemonStageProcess>.Instance;

        _refresher.Track(process.Document);
        return new IDaemonStageProcess[] { new Process(process, _repository) };
    }

    private static ConstructorInfo FindHighlightingConstructor()
    {
        var constructor = typeof(IUnitTestElementHighlighting).Assembly.GetType(HighlightingTypeName)?
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(IUnitTestLikeElementDisposition), typeof(DocumentRange) },
                null);
        if (constructor == null || !typeof(IHighlighting).IsAssignableFrom(constructor.DeclaringType))
        {
            Log.Warn(
                $"{HighlightingTypeName}(IUnitTestLikeElementDisposition, DocumentRange) not found in this Rider " +
                "release: .feature files get no test gutter marks.");
            return null;
        }

        return constructor;
    }

    private sealed class Process : IDaemonStageProcess
    {
        private readonly IUnitTestElementRepository _repository;

        public Process(IDaemonProcess daemonProcess, IUnitTestElementRepository repository)
        {
            DaemonProcess = daemonProcess;
            _repository = repository;
        }

        public IDaemonProcess DaemonProcess { get; }

        public void Execute(Action<DaemonStageResult> committer)
        {
            var highlightings = new List<HighlightingInfo>();
            try
            {
                Collect(highlightings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Adding test gutter marks to a .feature file failed");
                highlightings.Clear();
            }

            committer(new DaemonStageResult(highlightings));
        }

        private void Collect(List<HighlightingInfo> into)
        {
            var featureProjectFile = DaemonProcess.SourceFile.ToProjectFile();
            var codeBehind = CodeBehindOf(featureProjectFile, DaemonProcess.SourceFile.PsiModule.TargetFrameworkId);
            var codeBehindDocument = codeBehind?.ToSourceFile()?.Document;
            if (featureProjectFile == null || codeBehindDocument == null)
                return;

            List<IUnitTestElement> elements;
            using (UT.ReadLock())
                elements = new List<IUnitTestElement>(_repository.Query(new ProjectFileCriterion(codeBehind)));
            if (elements.Count == 0)
                return;

            var feature = new FeatureDocument(DaemonProcess.Document);
            var lines = new TestLines(feature, codeBehind, codeBehindDocument);
            var marked = new HashSet<int>();
            foreach (var element in elements)
            {
                if (DaemonProcess.InterruptFlag)
                    throw new OperationCanceledException();

                var line = lines.LineOf(element);
                if (line < 0 || line >= feature.LineCount || !marked.Add(line))
                    continue;

                into.Add(Mark(element, featureProjectFile, feature, line));
            }

            Log.Verbose($"{featureProjectFile.Name}: {elements.Count} test(s) in the code-behind, {into.Count} mark(s)");
        }

        private static HighlightingInfo Mark(IUnitTestElement element, IProjectFile featureProjectFile, FeatureDocument feature, int line)
        {
            var (start, end) = feature.LineRange(line);
            var text = feature.Line(line);
            var contentStart = start + (text.Length - text.TrimStart().Length);

            var location = new UnitTestElementLocation(
                featureProjectFile, new TextRange(contentStart, end), new TextRange(start, end));
            var markRange = new DocumentRange(feature.Document, new TextRange(contentStart));
            var highlighting = (IHighlighting)HighlightingConstructor.Value.Invoke(
                new object[] { new UnitTestElementDisposition(element, location), markRange });
            return new HighlightingInfo(markRange, highlighting);
        }

        /// <summary>
        /// The code-behind Reqnroll generated for <paramref name="featureProjectFile"/>: next to it, or under the
        /// intermediate output path when the project sets <c>ReqnrollUseIntermediateOutputPathForCodeBehind</c>
        /// (<see cref="CodeBehindPaths"/>), with the properties read for the feature's target framework. Rider's project
        /// model holds the generated file either way.
        /// </summary>
        private static IProjectFile CodeBehindOf(IProjectFile featureProjectFile, TargetFrameworkId targetFramework)
        {
            var project = featureProjectFile?.GetProject();
            var projectDirectory = project?.ProjectFileLocation.Directory;
            if (project == null || projectDirectory == null || projectDirectory.IsEmpty)
                return null;

            var name = featureProjectFile.Name + ".cs";
            var useIntermediateOutputPath = string.Equals(
                project.GetRequestedProjectProperty(targetFramework, CodeBehindPaths.UseIntermediateOutputPathProperty),
                "true", StringComparison.OrdinalIgnoreCase);
            if (!useIntermediateOutputPath)
                return SiblingOf(featureProjectFile, name);

            // A stale code-behind next to the feature is not compiled when the property is set, so it is not looked for.
            var path = CodeBehindPaths.Locate(
                projectDirectory.FullPath,
                featureProjectFile.Location.FullPath,
                useIntermediateOutputPath,
                project.GetRequestedProjectProperty(targetFramework, CodeBehindPaths.IntermediateOutputPathProperty));
            if (path == null)
                return null;

            foreach (var file in project.GetAllProjectFiles(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (CodeBehindPaths.SamePath(file.Location.FullPath, path))
                    return file;
            }

            return null;
        }

        private static IProjectFile SiblingOf(IProjectFile featureProjectFile, string name)
        {
            var siblings = featureProjectFile.ParentFolder?.GetSubItems(name);
            if (siblings == null)
                return null;
            foreach (var item in siblings)
            {
                if (item is IProjectFile file)
                    return file;
            }

            return null;
        }
    }

    /// <summary>Line access to the <c>.feature</c> document.</summary>
    private sealed class FeatureDocument
    {
        public FeatureDocument(IDocument document)
        {
            Document = document;
            LineCount = (int)document.GetLineCount();
        }

        public IDocument Document { get; }

        public int LineCount { get; }

        public (int start, int end) LineRange(int line)
        {
            var docLine = (Int32<DocLine>)line;
            return (Document.GetLineStartOffset(docLine), Document.GetLineEndOffsetNoLineBreak(docLine));
        }

        public string Line(int line)
        {
            if (line < 0 || line >= LineCount)
                return string.Empty;
            var (start, end) = LineRange(line);
            return Document.GetText(new TextRange(start, end));
        }
    }

    /// <summary>
    /// Finds the <c>.feature</c> line of each generated test. The test class (a root element) maps to the
    /// <c>Feature</c> line, a test method to its scenario's first <c>#line</c>, and a data row of an outline's method
    /// to its example row by its position among the method's row attributes. Results per method are cached for the
    /// rows that share it.
    /// </summary>
    private sealed class TestLines
    {
        private readonly FeatureDocument _feature;
        private readonly IProjectFile _codeBehind;
        private readonly IDocument _codeBehindDocument;
        private readonly Dictionary<IUnitTestElement, Outline> _outlines = new Dictionary<IUnitTestElement, Outline>();
        private int? _featureLine;

        public TestLines(FeatureDocument feature, IProjectFile codeBehind, IDocument codeBehindDocument)
        {
            _feature = feature;
            _codeBehind = codeBehind;
            _codeBehindDocument = codeBehindDocument;
        }

        public int LineOf(IUnitTestElement element)
        {
            switch (Depth(element))
            {
                case 0:
                    return _featureLine ??= GeneratedTestLines.FeatureLine(_feature.Line, _feature.LineCount);
                case 1:
                    return GeneratedTestLines.ScenarioLine(DeclarationText(element));
                case 2:
                    return RowLine(element);
                default:
                    return -1;
            }
        }

        private int RowLine(IUnitTestElement row)
        {
            var outline = OutlineOf(row.Parent);
            if (outline.Rows.Count == 0)
                return -1;

            // The row's own location, relative to its method's declaration: NUnit and xUnit locate a row at its
            // attribute. MSTest rows have none and are told by display name.
            var rowRange = DeclarationRange(row);
            int? rowOffset = rowRange.IsValid && outline.Range.IsValid ? rowRange.StartOffset - outline.Range.StartOffset : (int?)null;
            var position = GeneratedTestLines.RowPosition(
                outline.Rows, rowOffset, rowRange.IsValid ? _codeBehindDocument.GetText(rowRange) : null, row.ShortName);
            return GeneratedTestLines.RowLine(position, outline.RowLines);
        }

        private Outline OutlineOf(IUnitTestElement method)
        {
            if (_outlines.TryGetValue(method, out var outline))
                return outline;

            var range = DeclarationRange(method);
            var declaration = range.IsValid ? _codeBehindDocument.GetText(range) : string.Empty;
            var scenarioLine = GeneratedTestLines.ScenarioLine(declaration);
            var rows = GeneratedTestLines.RowAttributes(declaration);
            var rowLines = scenarioLine < 0 || rows.Count == 0
                ? (IReadOnlyList<int>)Array.Empty<int>()
                : GeneratedTestLines.ExampleRowLines(
                    _feature.Line, _feature.LineCount,
                    GeneratedTestLines.LastStepLine(declaration, scenarioLine), rows.Count);

            outline = new Outline(range, rows, rowLines);
            _outlines[method] = outline;
            return outline;
        }

        /// <summary>The text of the element's declaration in the code-behind, or empty if it has none there.</summary>
        private string DeclarationText(IUnitTestElement element)
        {
            var range = DeclarationRange(element);
            return range.IsValid ? _codeBehindDocument.GetText(range) : string.Empty;
        }

        /// <summary>The range of the element's declaration in the code-behind; invalid if it has none there.</summary>
        private TextRange DeclarationRange(IUnitTestElement element)
        {
            var locations = element.GetLocations();
            if (locations == null)
                return TextRange.InvalidRange;
            foreach (var location in locations)
            {
                if (location.ProjectFile.Equals(_codeBehind) && location.ContainingRange.IsValid)
                    return location.ContainingRange;
            }

            return TextRange.InvalidRange;
        }

        private static int Depth(IUnitTestElement element)
        {
            var depth = 0;
            for (var parent = element.Parent; parent != null; parent = parent.Parent)
                depth++;
            return depth;
        }
    }

    private sealed class Outline
    {
        public Outline(TextRange range, IReadOnlyList<GeneratedRow> rows, IReadOnlyList<int> rowLines)
        {
            Range = range;
            Rows = rows;
            RowLines = rowLines;
        }

        /// <summary>The method's declaration range in the code-behind.</summary>
        public TextRange Range { get; }

        /// <summary>The method's row attributes, in declaration order (the example rows' order).</summary>
        public IReadOnlyList<GeneratedRow> Rows { get; }

        public IReadOnlyList<int> RowLines { get; }
    }
}
