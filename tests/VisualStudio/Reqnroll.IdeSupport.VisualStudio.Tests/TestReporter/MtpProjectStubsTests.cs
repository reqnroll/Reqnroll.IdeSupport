using System;
using System.Collections.Generic;
using System.IO;
using AwesomeAssertions;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestReporter;

/// <summary>
/// Covers <see cref="MtpProjectStubs"/> — the project-local <c>obj\&lt;Project&gt;.csproj.reqnroll-ide.targets</c>
/// stub (issue #741) that replaced the per-user <c>ImportAfter</c> file-drop. What the stub's import
/// actually does to a build is covered by <c>SourceInjectionBuildTests</c> in
/// Reqnroll.IdeSupport.TestReporter.MTP.Tests; this covers where and when the VS extension writes it.
/// </summary>
public sealed class MtpProjectStubsTests : IDisposable
{
    private const string BundleTargets = @"C:\ext\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.targets";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-project-stubs-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateProject(string relativePath, string xml = "<Project Sdk=\"Microsoft.NET.Sdk\" />")
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }

    private static Func<string, string?> NoEvaluation => _ => throw new InvalidOperationException("must not evaluate on the fast path");

    // ── BuildStubXml ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildStubXml_is_an_Exists_guarded_import_of_the_bundle_and_nothing_else()
    {
        var xml = MtpProjectStubs.BuildStubXml(BundleTargets);

        xml.Should().Contain($"<Import Project=\"{BundleTargets}\" Condition=\"Exists('{BundleTargets}')\" />");
        xml.Should().NotContain("<Reference").And.NotContain("<ItemGroup").And.NotContain("TestingPlatformBuilderHook",
            "all gating and injection lives in the bundle's .targets file, shared by every IDE");
    }

    // ── ResolveProjectExtensionsDirectory ────────────────────────────────────

    [Fact]
    public void An_ordinary_project_uses_its_obj_directory_without_an_MSBuild_evaluation()
    {
        var project = CreateProject(@"src\App\App.csproj");

        MtpProjectStubs.ResolveProjectExtensionsDirectory(project, ReadOrNull, NoEvaluation)
            .Should().Be(Path.Combine(_dir, "src", "App", "obj"));
    }

    [Theory]
    [InlineData("<BaseIntermediateOutputPath>..\\intermediate\\</BaseIntermediateOutputPath>")]
    [InlineData("<MSBuildProjectExtensionsPath>ext\\</MSBuildProjectExtensionsPath>")]
    [InlineData("<UseArtifactsOutput>true</UseArtifactsOutput>")]
    [InlineData("<ArtifactsPath>$(MSBuildThisFileDirectory)out</ArtifactsPath>")]
    public void A_Directory_Build_props_that_moves_obj_defers_to_MSBuild(string property)
    {
        File.WriteAllText(Path.Combine(CreateDir("repo"), "Directory.Build.props"), $"<Project><PropertyGroup>{property}</PropertyGroup></Project>");
        var project = CreateProject(@"repo\src\App\App.csproj");
        var evaluated = Path.Combine(_dir, "repo", "artifacts", "obj", "App") + Path.DirectorySeparatorChar;

        MtpProjectStubs.ResolveProjectExtensionsDirectory(project, ReadOrNull, _ => evaluated).Should().Be(evaluated);
    }

    [Fact]
    public void A_failed_evaluation_means_no_directory_rather_than_a_guess()
    {
        var project = CreateProject(@"App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><BaseIntermediateOutputPath>x\\</BaseIntermediateOutputPath></PropertyGroup></Project>");

        MtpProjectStubs.ResolveProjectExtensionsDirectory(project, ReadOrNull, _ => null).Should().BeNull();
    }

    // ── TryWriteStub ─────────────────────────────────────────────────────────

    [Fact]
    public void TryWriteStub_writes_obj_Project_csproj_reqnroll_ide_targets()
    {
        var project = CreateProject(@"App\App.csproj");

        var stub = MtpProjectStubs.TryWriteStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation);

        stub.Should().Be(Path.Combine(_dir, "App", "obj", "App.csproj.reqnroll-ide.targets"));
        File.ReadAllText(stub!).Should().Be(MtpProjectStubs.BuildStubXml(BundleTargets));
    }

    [Fact]
    public void TryWriteStub_leaves_an_up_to_date_stub_untouched()
    {
        var project = CreateProject(@"App\App.csproj");
        var stub = MtpProjectStubs.TryWriteStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation)!;
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(stub, stamp);

        MtpProjectStubs.TryWriteStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation).Should().Be(stub);

        File.GetLastWriteTimeUtc(stub).Should().Be(stamp, "rewriting an unchanged stub would disturb incremental builds");
    }

    [Fact]
    public void TryWriteStub_refreshes_a_stub_pointing_at_an_older_extension_install()
    {
        var project = CreateProject(@"App\App.csproj");
        MtpProjectStubs.TryWriteStub(project, @"C:\old\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.targets", _logger, ReadOrNull, NoEvaluation);

        var stub = MtpProjectStubs.TryWriteStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation)!;

        File.ReadAllText(stub).Should().Contain(BundleTargets).And.NotContain(@"C:\old\");
    }

    [Theory]
    [InlineData(@"App\App.vbproj")]
    [InlineData(@"App\App.fsproj")]
    public void TryWriteStub_skips_non_CSharp_projects(string relativePath)
    {
        var project = CreateProject(relativePath);

        MtpProjectStubs.TryWriteStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation).Should().BeNull();
        Directory.Exists(Path.Combine(_dir, "App", "obj")).Should().BeFalse();
    }

    [Fact]
    public void WriteStubs_writes_one_stub_per_CSharp_project_and_only_inside_each_project()
    {
        var a = CreateProject(@"A\A.csproj");
        var b = CreateProject(@"B\B.csproj");
        var vb = CreateProject(@"C\C.vbproj");

        MtpProjectStubs.WriteStubs(new List<string> { a, b, vb }, BundleTargets, _logger).Should().Be(2);

        File.Exists(Path.Combine(_dir, "A", "obj", "A.csproj.reqnroll-ide.targets")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "B", "obj", "B.csproj.reqnroll-ide.targets")).Should().BeTrue();
        Directory.GetFiles(_dir, "*.reqnroll-ide.targets", SearchOption.AllDirectories).Should().HaveCount(2);
    }

    // ── TryRemoveLegacyImportAfterFile ───────────────────────────────────────

    [Fact]
    public void TryRemoveLegacyImportAfterFile_deletes_only_our_old_per_user_file()
    {
        var importAfter = CreateDir("ImportAfter");
        var legacy = Path.Combine(importAfter, "Reqnroll.IdeSupport.TestReporter.MTP.g.targets");
        var someoneElses = Path.Combine(importAfter, "Other.Tool.targets");
        File.WriteAllText(legacy, "<Project />");
        File.WriteAllText(someoneElses, "<Project />");

        MtpProjectStubs.TryRemoveLegacyImportAfterFile(_logger, importAfter).Should().BeTrue();

        File.Exists(legacy).Should().BeFalse();
        File.Exists(someoneElses).Should().BeTrue();
        MtpProjectStubs.TryRemoveLegacyImportAfterFile(_logger, importAfter).Should().BeFalse("nothing left to remove");
    }

    private string CreateDir(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
