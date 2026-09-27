#nullable enable

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Reads whether the solution is open.
/// </summary>
/// <remarks>
/// Issue #774: this used to read property <c>0x0000000B</c>, which is not a valid <see cref="__VSPROPID"/>.
/// Every call failed, so the package's solution-load wait always ran to its limit (~10 s), delaying
/// "Solution loaded" and everything gated on it (MTP stubs, the Welcome dialog, and the #533
/// scratch-file activation trigger's grace period).
/// </remarks>
internal static class SolutionOpenState
{
    /// <summary>
    /// True when <paramref name="solution"/> reports <see cref="__VSPROPID.VSPROPID_IsSolutionOpen"/>.
    /// False when the property is false or cannot be read.
    /// </summary>
    public static bool IsOpen(IVsSolution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var hresult = solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out var isOpen);
        return FromPropertyResult(hresult, isOpen);
    }

    /// <summary>
    /// The decision rule alone, given the raw <c>GetProperty</c> result — split out so it is testable
    /// without a live <see cref="IVsSolution"/> or the UI thread.
    /// </summary>
    internal static bool FromPropertyResult(int hresult, object? propertyValue) =>
        ErrorHandler.Succeeded(hresult) && propertyValue is true;
}
