#nullable enable

using System;
using Microsoft.VisualStudio.Language.CodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Marks the location where a line-keyed classic-CodeLens indicator should render — shared by every
/// Reqnroll classic CodeLens feature (issue #372/#262). An ordinary editor tag — <see cref="ICodeLensTag"/>
/// requires no code-element/Roslyn model, only an <see cref="Microsoft.VisualStudio.Text.Tagging.ITagger{T}"/>
/// targeting the buffer's content type. Extracted from what were two near-identical copies
/// (<c>HookCodeLensTag</c>, <c>RunTestCodeLensTag</c>).
/// </summary>
/// <remarks>
/// Implements <see cref="ICodeLensTag2"/>, not just <see cref="ICodeLensTag"/> — see
/// <see cref="LineCodeLensDescriptor"/>'s remarks for why the plain v1 interface never resolves a
/// data point in this VS build.
/// </remarks>
internal sealed class LineCodeLensTag : ICodeLensTag2
{
    private readonly LineCodeLensDescriptor _descriptor;

    public LineCodeLensTag(LineCodeLensDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public ICodeLensDescriptor Descriptor => _descriptor;

    public ICodeLensDescriptorContextProvider DescriptorContextProvider => _descriptor;

    /// <inheritdoc />
    public event EventHandler? Disconnected;

    /// <summary>Raised when this tag is no longer part of the editor (e.g. the buffer closed).</summary>
    internal void RaiseDisconnected() => Disconnected?.Invoke(this, EventArgs.Empty);
}
