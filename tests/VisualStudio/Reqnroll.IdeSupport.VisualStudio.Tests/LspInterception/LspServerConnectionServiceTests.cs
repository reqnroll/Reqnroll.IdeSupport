using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="LspServerConnectionService"/> launches a process, wires
/// <c>Nerdbank.Streams</c> pipes, and starts its eager startup task via
/// <c>ThreadHelper.JoinableTaskFactory</c> from its constructor — all of that requires a VS host
/// and is not unit-testable here (see project convention noted in
/// <c>CodeLensRefreshInterceptorTests</c>). <see cref="LspServerConnectionService.ResolveServerExePath"/>
/// was extracted as a pure static method specifically so the one piece of testable logic (bundled
/// server exe path resolution) has coverage independent of process/VS-host concerns.
/// The start-failure cleanup test (issue #1024) uses the internal constructor's launch seams instead:
/// a stand-in <c>cmd.exe</c> for the server and a pipe factory that throws.
/// </summary>
public class LspServerConnectionServiceTests
{
    [Fact]
    public void Resolves_the_server_exe_under_an_LSPServer_subfolder_of_the_extension_assembly()
    {
        var extensionAssemblyLocation = @"C:\ext\Reqnroll.IdeSupport.VisualStudio.Extension.dll";

        var path = LspServerConnectionService.ResolveServerExePath(extensionAssemblyLocation);

        path.Should().Be(@"C:\ext\LSPServer\Reqnroll.IdeSupport.LSP.Server.exe");
    }

    [Fact]
    public void Resolution_is_relative_to_the_assembly_directory_not_the_working_directory()
    {
        var extensionAssemblyLocation = @"D:\some\other\deep\path\Ext.dll";

        var path = LspServerConnectionService.ResolveServerExePath(extensionAssemblyLocation);

        path.Should().Be(@"D:\some\other\deep\path\LSPServer\Reqnroll.IdeSupport.LSP.Server.exe");
    }

    [Fact]
    public void Server_arguments_identify_the_ide_and_set_all_three_logging_dials()
    {
        // The test assembly and the extension assembly share the same build configuration, so the
        // #if DEBUG branch actually taken here is whatever configuration this test itself was
        // compiled under.
#if DEBUG
        LspServerConnectionService.ServerArguments.Should().Be(
            "--ide visualstudio --log-level Verbose --protocol-log-level Info --trace Verbose");
#else
        LspServerConnectionService.ServerArguments.Should().Be(
            "--ide visualstudio --log-level Warning --protocol-log-level Warning --trace Off");
#endif
    }

    [Theory]
    // disposed, shutdownObserved, pipeTerminated, expected
    [InlineData(false, false, false, true)]   // process died with the pipe still live: a crash
    [InlineData(false, true, false, false)]   // VS's client sent shutdown first: a normal end
    [InlineData(false, false, true, false)]   // exit already went out (pipe marked terminated): a normal end (issue #555)
    [InlineData(true, false, false, false)]   // we are disposing: our own teardown, not a failure
    public void IsUnexpectedExit_only_flags_a_dead_server_the_client_did_not_ask_to_stop(
        bool disposed, bool shutdownObserved, bool pipeTerminated, bool expected)
    {
        LspServerConnectionService.IsUnexpectedExit(disposed, shutdownObserved, pipeTerminated).Should().Be(expected);
    }

    [Fact]
    public void IsUnexpectedExit_is_false_when_the_pipe_has_already_been_discarded()
    {
        LspServerConnectionService.IsUnexpectedExit(disposed: false, shutdownObserved: false, pipeTerminated: null)
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_start_that_fails_after_the_process_started_stops_the_process_and_releases_its_resources()
    {
        // ThreadHelper.JoinableTaskFactory is null outside a VS host, so the test supplies its own context.
#pragma warning disable VSSDK005 // No VS host here: there is no ThreadHelper singleton to share.
        var joinableTaskFactory = new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
        var launches = 0;
        Process? watcher = null;
        LspInspectorLogger? inspectorLogger = null;

        var service = new LspServerConnectionService(
            NullLogger<LspServerConnectionService>.Instance,
            NullLoggerFactory.Instance,
            new StepCodeLensState(),
            joinableTaskFactory,
            // Any existing file satisfies the executable-exists check; startProcess ignores it.
            serverExe: typeof(LspServerConnectionServiceTests).Assembly.Location,
            startProcess: psi =>
            {
                launches++;
                // Stand-in server: cmd.exe with redirected stdin waits for input until it is killed.
                psi.FileName  = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = "/d /q";
                var process = Process.Start(psi)!;
                // A separate handle, so the test can still observe the process after the service disposes its own.
                watcher = Process.GetProcessById(process.Id);
                return process;
            },
            createInterceptingPipe: (_, sendInterceptors, _, _) =>
            {
                inspectorLogger = sendInterceptors.OfType<LspInspectorLogger>().Single();
                throw new InvalidOperationException("Simulated pipe construction failure (issue #1024).");
            });

        try
        {
            var connection = await service.GetConnectionAsync();
            var retry      = await service.GetConnectionAsync();

            using (new AssertionScope())
            {
                connection.Should().BeNull();
                retry.Should().BeNull();
                launches.Should().Be(1, "a failed launch is still not retried");

                watcher.Should().NotBeNull();
                watcher!.WaitForExit(10_000).Should().BeTrue("the server process started by the failed launch must be stopped");

                service.HoldsServerResources.Should().BeFalse("the failed launch's process, job and inspector log must be released");

                // The inspector log is held open without FileShare.Delete until the logger is disposed.
                inspectorLogger.Should().NotBeNull();
                var deleteInspectorLog = () => File.Delete(inspectorLogger!.LogFilePath);
                deleteInspectorLog.Should().NotThrow("the failed launch's inspector logger must be disposed");
            }
        }
        finally
        {
            service.Dispose();
            try { if (watcher is { HasExited: false }) watcher.Kill(); } catch { /* best-effort */ }
            watcher?.Dispose();
        }
    }
}
