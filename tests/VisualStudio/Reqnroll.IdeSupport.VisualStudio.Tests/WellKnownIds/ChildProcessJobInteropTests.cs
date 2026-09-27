using System.Runtime.InteropServices;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// <c>SetInformationJobObject</c> rejects a buffer whose size does not match the Win32
/// <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>, and then the LSP server outlives VS. Guards the
/// hand-written struct layout against the documented sizes.
/// </summary>
public class ChildProcessJobInteropTests
{
    [Fact]
    public void Extended_limit_information_has_the_Win32_size()
    {
        Marshal.SizeOf<ChildProcessJob.NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
            .Should().Be(IntPtr.Size == 8 ? 144 : 112);
    }

    [Fact]
    public void Io_counters_has_the_Win32_size()
    {
        Marshal.SizeOf<ChildProcessJob.NativeMethods.IO_COUNTERS>().Should().Be(48);
    }
}
