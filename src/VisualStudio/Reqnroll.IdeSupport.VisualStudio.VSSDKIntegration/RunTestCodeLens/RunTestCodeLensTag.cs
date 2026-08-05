#nullable enable

using System;
using Microsoft.VisualStudio.Language.CodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Marks the location where a Run CodeLens indicator should render (design doc §5/§6, issue #262).
/// Mirrors <c>HookCodeLensTag</c> exactly.
/// </summary>
internal sealed class RunTestCodeLensTag : ICodeLensTag2
{
    private readonly RunTestCodeLensDescriptor _descriptor;

    public RunTestCodeLensTag(RunTestCodeLensDescriptor descriptor)
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
