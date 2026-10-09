using System.Runtime.InteropServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.VisualStudio.Extension.Documents;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Issue #1022: <c>IVsRunningDocumentTable.GetDocumentInfo</c> hands back an AddRef'd
/// <c>IUnknown</c> in <c>ppunkDocData</c> (the shell's own <c>RunningDocumentTable</c> wrapper
/// releases it after use), so the RDT scan must release it for every document it visits.
/// </summary>
public class VsStubFrameInitializerTests
{
    [ComVisible(true)]
    public sealed class DocDataStub { }

    [Theory]
    [InlineData(@"C:\repo\StepDefinitions\LoginSteps.cs")] // filtered out by moniker
    [InlineData(@"C:\repo\Features\Login.feature")]        // "already initialised" skip branch
    public void Releases_the_docData_reference_handed_out_by_GetDocumentInfo(string moniker)
    {
        var docData = new DocDataStub();
        var held = Marshal.GetIUnknownForObject(docData);    // the test's own reference
        var handedOut = Marshal.GetIUnknownForObject(docData); // the reference the RDT "hands out"
        held.Should().Be(handedOut);
        try
        {
            var rdt = CreateRdt(moniker, handedOut);

            RunAsUiThread(() =>
                VsStubFrameInitializer.TryForceInitRdtStubs(rdt, serviceProvider: null!, NullLogger.Instance));

            // Count excluding the test's own reference: 1 means the scan released what it was given.
            var count = Marshal.AddRef(held);
            Marshal.Release(held);
            (count - 1).Should().Be(1, "the scan must Release the docData pointer it received");
        }
        finally
        {
            Marshal.Release(held);
        }
    }

    // TryForceInitRdtStubs asserts it runs on the UI thread. ThreadHelper binds that thread to a
    // dispatcher via an internal SetUIThread(); under xUnit that is never called, so bind the
    // calling thread for the duration of the call (test-only reflection).
    private static void RunAsUiThread(Action action)
    {
        var field = typeof(ThreadHelper).GetField("uiThreadDispatcher",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull("ThreadHelper's UI thread dispatcher field is needed to simulate the UI thread");
        var original = field!.GetValue(null);
        field.SetValue(null, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        try
        {
            action();
        }
        finally
        {
            field.SetValue(null, original);
        }
    }
    private static IVsRunningDocumentTable CreateRdt(string moniker, IntPtr docData)
    {
        var rdt = Substitute.For<IVsRunningDocumentTable>();

        var docs = Substitute.For<IEnumRunningDocuments>();
        var served = false;
        docs.Next(1, Arg.Any<uint[]>(), out Arg.Any<uint>())
            .Returns(call =>
            {
                if (served)
                {
                    call[2] = 0u;
                    return 1; // S_FALSE
                }
                served = true;
                ((uint[])call[1])[0] = 42u;
                call[2] = 1u;
                return 0;
            });
        rdt.GetRunningDocumentsEnum(out Arg.Any<IEnumRunningDocuments>())
            .Returns(call =>
            {
                call[0] = docs;
                return 0;
            });

        rdt.GetDocumentInfo(42u, out Arg.Any<uint>(), out Arg.Any<uint>(), out Arg.Any<uint>(),
                out Arg.Any<string>(), out Arg.Any<IVsHierarchy>(), out Arg.Any<uint>(), out Arg.Any<IntPtr>())
            .Returns(call =>
            {
                call[4] = moniker;
                call[7] = docData;
                return 0;
            });
        return rdt;
    }
}
