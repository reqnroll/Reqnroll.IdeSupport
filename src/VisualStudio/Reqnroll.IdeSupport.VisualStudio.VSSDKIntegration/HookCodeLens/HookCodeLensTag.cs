#nullable enable

using System;
using Microsoft.VisualStudio.Language.CodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Marks the location where a hook-match-count CodeLens indicator should render (issue #372).
/// An ordinary editor tag — <see cref="ICodeLensTag"/> requires no code-element/Roslyn model, only
/// an <see cref="Microsoft.VisualStudio.Text.Tagging.ITagger{T}"/> targeting the buffer's content type.
/// </summary>
internal sealed class HookCodeLensTag : ICodeLensTag
{
    public HookCodeLensTag(ICodeLensDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public ICodeLensDescriptor Descriptor { get; }

    /// <inheritdoc />
    public event EventHandler? Disconnected;

    /// <summary>Raised when this tag is no longer part of the editor (e.g. the buffer closed).</summary>
    internal void RaiseDisconnected() => Disconnected?.Invoke(this, EventArgs.Empty);
}
