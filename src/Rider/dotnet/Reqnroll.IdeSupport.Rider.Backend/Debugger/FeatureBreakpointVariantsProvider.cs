using System.Collections.Generic;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Debugger;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

namespace Reqnroll.IdeSupport.Rider.Backend.Debugger;

/// <summary>
/// Offers a plain line breakpoint on <c>.feature</c> lines. Rider's debugger binds it through the
/// <c>#line</c> directives Reqnroll writes into the generated test code, the same way it binds Razor
/// and T4 breakpoints.
/// </summary>
[Language(typeof(FeatureLanguage))]
public class FeatureBreakpointVariantsProvider : IBreakpointVariantsProvider
{
    private static readonly IReadOnlyList<IBreakpoint> None = new IBreakpoint[0];

    public IReadOnlyList<IBreakpoint> GetBreakpointVariants(IProjectFile file, int line, ISolution solution)
    {
        if (line < 0)
            return None;

        foreach (var sourceFile in solution.PsiModules().GetPsiSourceFilesFor(file))
        {
            var document = sourceFile.Document;
            var lineCount = (int)document.GetLineCount();
            if (line >= lineCount)
                return None;

            var docLine = (Int32<DocLine>)line;
            var lineText = document.GetText(new TextRange(
                document.GetLineStartOffset(docLine),
                document.GetLineEndOffsetNoLineBreak(docLine)));

            return BreakpointLineFilter.IsBreakpointCandidate(lineText)
                ? new IBreakpoint[] { new LineBreakpoint() }
                : None;
        }

        return None;
    }

    public IEnumerable<string> GetSupportedFileExtensions()
    {
        yield return FeatureProjectFileType.FeatureExtension;
    }
}
