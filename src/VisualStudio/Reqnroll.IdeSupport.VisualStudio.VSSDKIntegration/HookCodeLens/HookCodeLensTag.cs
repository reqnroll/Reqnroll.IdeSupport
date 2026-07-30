#nullable enable

using System;
using Microsoft.VisualStudio.Language.CodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Marks the location where a hook-match-count CodeLens indicator should render (issue #372).
/// An ordinary editor tag — <see cref="ICodeLensTag"/> requires no code-element/Roslyn model, only
/// an <see cref="Microsoft.VisualStudio.Text.Tagging.ITagger{T}"/> targeting the buffer's content type.
/// </summary>
/// <remarks>
/// Implements <see cref="ICodeLensTag2"/>, not just <see cref="ICodeLensTag"/> — see
/// <see cref="HookCodeLensDescriptor"/>'s remarks for why the plain v1 interface never resolves a
/// data point in this VS build.
/// </remarks>
internal sealed class HookCodeLensTag : ICodeLensTag2
{
    private readonly HookCodeLensDescriptor _descriptor;

    public HookCodeLensTag(HookCodeLensDescriptor descriptor)
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
