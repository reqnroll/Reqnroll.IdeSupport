using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Logging;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.Logging;

/// <summary>
/// Issue #748: each process must have exactly one <see cref="SynchronousFileLogger"/> per log file.
/// Two instances on the same file each serialize only with their own lock, so concurrent writes hit
/// a sharing violation and a line is silently dropped. These pin the two shared instances that
/// replace the four ad-hoc ones: <see cref="ExtensionHostLogger"/> (devenv.exe, both composition
/// roots) and <see cref="CodeLensHostLogger"/> (the CodeLens ServiceHub host).
/// </summary>
public class SharedHostLoggerTests
{
    [Fact]
    public void MEF_export_hands_out_the_same_instance_the_DI_side_registers()
    {
        // ExtensionEntrypoint registers ExtensionHostLogger.Instance with VS.Extensibility DI; every
        // MEF-composed export provider must hand back that same object, not a composite of its own.
        new IdeSupportLoggerExportProvider().Logger.Should().BeSameAs(ExtensionHostLogger.Instance);
        new IdeSupportLoggerExportProvider().Logger.Should().BeSameAs(ExtensionHostLogger.Instance);
    }

    [Fact]
    public void Extension_host_logger_has_one_ext_file_logger_at_Info_and_the_output_pane()
    {
        var composite = ExtensionHostLogger.Instance.Should().BeOfType<IdeSupportCompositeLogger>().Subject;

        var fileLogger = composite.OfType<SynchronousFileLogger>().Should().ContainSingle().Subject;
        Path.GetFileName(fileLogger.LogFilePath).Should().StartWith("reqnroll-vs-ext-");
        fileLogger.Level.Should().Be(EffectiveLevel(TraceLevel.Info));

        composite.OfType<VsOutputPaneLogger>().Should().ContainSingle();
    }

    [Fact]
    public void CodeLens_host_logger_is_a_file_only_Verbose_logger_with_its_own_role()
    {
        // File-only: the ServiceHub host has no VS shell, so no output pane sink belongs here.
        var fileLogger = CodeLensHostLogger.Instance.Should().BeOfType<SynchronousFileLogger>().Subject;

        Path.GetFileName(fileLogger.LogFilePath).Should().StartWith("reqnroll-vs-codelens-sh-");
        fileLogger.Level.Should().Be(EffectiveLevel(TraceLevel.Verbose));
    }

    [Fact]
    public void The_two_hosts_write_to_different_files_even_within_one_process()
    {
        var extFile = ExtensionHostLogger.Instance.Should().BeOfType<IdeSupportCompositeLogger>().Subject
            .OfType<SynchronousFileLogger>().Single().LogFilePath;
        var codeLensFile = ((SynchronousFileLogger)CodeLensHostLogger.Instance).LogFilePath;

        codeLensFile.Should().NotBe(extFile);
    }

    // REQNROLLVS_DEBUG (commonly set on dev machines) overrides every SynchronousFileLogger's
    // configured level, so compare against what a fresh logger configured at the same level gets in
    // this environment rather than the raw constant.
    private static TraceLevel EffectiveLevel(TraceLevel configured)
        => new SynchronousFileLogger("vs", "test", configured).Level;
}
