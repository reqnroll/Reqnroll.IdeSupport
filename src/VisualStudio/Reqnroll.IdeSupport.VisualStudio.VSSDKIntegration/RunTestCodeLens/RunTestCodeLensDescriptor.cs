#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Describes one Run-lens location for the classic CodeLens API (design doc §5/§6, issue #262).
/// Mirrors <c>HookCodeLensDescriptor</c> exactly — see its remarks for why
/// <see cref="ICodeLensDescriptorContextProvider"/> must be implemented directly on the descriptor
/// (a plain v1 <c>ICodeLensTag</c> never resolves a data point in this VS build, issue #372).
/// </summary>
internal sealed class RunTestCodeLensDescriptor : ICodeLensDescriptor, ICodeLensDescriptorContextProvider
{
    private readonly CodeLensDescriptorContext _context;

    public RunTestCodeLensDescriptor(string filePath, Span applicableSpan, string elementDescription)
    {
        FilePath = filePath;
        ApplicableSpan = applicableSpan;
        ElementDescription = elementDescription;
        _context = new CodeLensDescriptorContext(applicableSpan);
    }

    public string FilePath { get; }

    /// <summary>Always <see cref="Guid.Empty"/> — a Run lens isn't tied to a specific project's compiled output at the descriptor level (the resolved target's own <c>OutputAssemblyPath</c> carries that).</summary>
    public Guid ProjectGuid => Guid.Empty;

    public string ElementDescription { get; }

    public Span? ApplicableSpan { get; }

    /// <summary>No <see cref="CodeElementKinds"/> value fits a Gherkin Scenario/Scenario Outline line; <c>Unspecified</c> is the documented value to use when there's no applicable kind.</summary>
    public CodeElementKinds Kind => CodeElementKinds.Unspecified;

    /// <inheritdoc />
    public Task<CodeLensDescriptorContext> GetCurrentContextAsync() => Task.FromResult(_context);
}
