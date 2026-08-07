#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Describes one line-keyed classic-CodeLens lens location — shared by every Reqnroll classic
/// CodeLens feature (hook-match-count, Run test — issue #372/#262), since none of them have a
/// code-element/symbol model behind a <c>.feature</c> line; the owning
/// <see cref="LineKeyedCodeLensTagger{TEntry}"/> supplies the span itself from live server data.
/// Extracted from what were two near-identical copies (<c>HookCodeLensDescriptor</c>,
/// <c>RunTestCodeLensDescriptor</c>).
/// </summary>
/// <remarks>
/// Also implements <see cref="ICodeLensDescriptorContextProvider"/> for itself. VS's classic-CodeLens
/// host (<c>CodeLensRpcDataPointProviderWrapper.TryCreateDataPointAsync</c>, decompiled while
/// debugging issue #372's "Unsupported CodeLens descriptor" exception) only resolves a descriptor's
/// context — and therefore only ever calls a provider's <c>CanCreateDataPointAsync</c>/
/// <c>CreateDataPointAsync</c> — when the owning <see cref="ICodeLensTag"/> is an
/// <see cref="ICodeLensTag2"/> exposing a <see cref="DescriptorContextProvider"/>. A plain
/// <see cref="ICodeLensTag"/> (v1) has no resolution path in that build at all and always throws.
/// </remarks>
internal sealed class LineCodeLensDescriptor : ICodeLensDescriptor, ICodeLensDescriptorContextProvider
{
    private readonly CodeLensDescriptorContext _context;

    public LineCodeLensDescriptor(string filePath, Span applicableSpan, string elementDescription)
    {
        FilePath = filePath;
        ApplicableSpan = applicableSpan;
        ElementDescription = elementDescription;
        _context = new CodeLensDescriptorContext(applicableSpan);
    }

    public string FilePath { get; }

    /// <summary>Always <see cref="Guid.Empty"/> — a line-keyed lens isn't tied to a specific project's compiled output at the descriptor level.</summary>
    public Guid ProjectGuid => Guid.Empty;

    public string ElementDescription { get; }

    public Span? ApplicableSpan { get; }

    /// <summary>No <see cref="CodeElementKinds"/> value fits a Gherkin line; <c>Unspecified</c> is the documented value to use when there's no applicable kind.</summary>
    public CodeElementKinds Kind => CodeElementKinds.Unspecified;

    /// <inheritdoc />
    public Task<CodeLensDescriptorContext> GetCurrentContextAsync() => Task.FromResult(_context);
}
