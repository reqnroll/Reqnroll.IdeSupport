#nullable enable

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Reads whether the solution is open. Split out from <see cref="ReqnrollPluginPackage"/> so it can
/// be tested without a running VS.
/// </summary>
internal static class SolutionOpenState
{
    /// <summary>
    /// True when <paramref name="solution"/> reports <see cref="__VSPROPID.VSPROPID_IsSolutionOpen"/>.
    /// False when the property is false or cannot be read.
    /// </summary>
    /// <remarks>
    /// Issue #774: this used to read property <c>0x0000000B</c>, which does not exist. Every call
    /// failed, so the package's solution-load wait always ran to its limit (~10 s).
    /// </remarks>
    public static bool IsOpen(IVsSolution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return ErrorHandler.Succeeded(solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out var isOpen))
               && isOpen is true;
    }
}
