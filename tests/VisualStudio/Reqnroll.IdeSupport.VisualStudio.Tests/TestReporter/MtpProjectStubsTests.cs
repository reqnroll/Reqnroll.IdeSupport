using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        xml.Should().Contain($"<_ReqnrollIdeMtpReporterBundle>{BundleTargets}</_ReqnrollIdeMtpReporterBundle>");
        xml.Should().Contain("<Import Project=\"$(_ReqnrollIdeMtpReporterBundle)\" Condition=\"Exists('$(_ReqnrollIdeMtpReporterBundle)')\" />");
        xml.Should().NotContain("<Reference").And.NotContain("<ItemGroup").And.NotContain("TestingPlatformBuilderHook",
            "all gating and injection lives in the bundle's .targets file, shared by every IDE");
    }

    [Fact]
    public void BuildStubXml_escapes_a_user_profile_path_that_would_otherwise_break_every_build()
    {
        var xml = MtpProjectStubs.BuildStubXml(@"C:\Users\O'Brien & $Co 100%;@x\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.targets");

        xml.Should().Contain(@"<_ReqnrollIdeMtpReporterBundle>C:\Users\O'Brien &amp; %24Co 100%25%3B%40x\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.targets</_ReqnrollIdeMtpReporterBundle>");
        System.Xml.Linq.XDocument.Parse(xml).Should().NotBeNull("the stub must stay well-formed XML");
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

    // ── Reqnroll projects only ───────────────────────────────────────────────

    /// <summary>A trimmed project.assets.json whose restore graph is <paramref name="libraries"/> ("Name/Version").</summary>
    private static string Assets(params string[] libraries)
    {
        var entries = string.Join("," + Environment.NewLine,
            libraries.Select(l => $$"""    "{{l}}": { "type": "package", "path": "{{l.ToLowerInvariant()}}" }"""));
        return $$"""
            {
              "version": 3,
              "targets": { "net10.0": {} },
              "libraries": {
            {{entries}}
              },
              "packageFolders": { "C:\\Users\\me\\.nuget\\packages\\": {} },
              "project": { "restore": { "projectName": "App", "projectPath": "C:\\src\\ReqnrollDemo\\App.csproj" } },
              "logs": [ { "code": "NU1603", "message": "Reqnroll/3.3.4" } ]
            }
            """;
    }

    private string CreateRestoredProject(string relativePath, params string[] libraries)
    {
        var project = CreateProject(relativePath);
        var obj = CreateDir(Path.Combine(Path.GetDirectoryName(relativePath)!, "obj"));
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), Assets(libraries));
        return project;
    }

    [Theory]
    [InlineData("Reqnroll.xunit.v3/3.3.3")]       // a runner plugin, referenced directly
    [InlineData("Reqnroll/3.3.4")]                // the runtime alone, e.g. arriving through an in-house meta-package
    [InlineData("MyCompany.ReqnrollSteps/1.0.0")] // a third-party package or project reference with the name in it
    [InlineData("reqnroll.mstest/3.3.4")]         // case-insensitive, like the LSP server's rule
    public void DetectReqnrollUsage_finds_Reqnroll_anywhere_in_the_restore_graph(string reqnrollLibrary)
    {
        MtpProjectStubs.DetectReqnrollUsage(Assets("MSTest.TestAdapter/4.2.3", reqnrollLibrary, "System.Text.Json/9.0.0"))
            .Should().BeTrue();
    }

    [Fact]
    public void DetectReqnrollUsage_is_false_without_Reqnroll_even_when_paths_and_messages_mention_it_and_unknown_before_restore()
    {
        MtpProjectStubs.DetectReqnrollUsage(Assets("MSTest.TestAdapter/4.2.3", "Microsoft.Testing.Platform/2.2.3"))
            .Should().BeFalse("the project path and the restore log mention Reqnroll, but no package or project reference does");
        MtpProjectStubs.DetectReqnrollUsage(null).Should().BeNull("with no restore output yet the answer is not known");
    }

    [Fact]
    public void TrySyncStub_writes_a_stub_for_a_restored_Reqnroll_project()
    {
        var project = CreateRestoredProject(@"App\App.csproj", "Reqnroll.MsTest/3.3.4", "Reqnroll/3.3.4");

        MtpProjectStubs.TrySyncStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation).Should().Be(MtpStubSyncResult.Written);

        File.ReadAllText(Path.Combine(_dir, "App", "obj", "App.csproj.reqnroll-ide.targets")).Should().Be(MtpProjectStubs.BuildStubXml(BundleTargets));
    }

    [Fact]
    public void TrySyncStub_writes_nothing_into_a_project_that_does_not_use_Reqnroll_and_removes_an_earlier_stub()
    {
        var project = CreateRestoredProject(@"App\App.csproj", "MSTest.TestAdapter/4.2.3");
        var stub = Path.Combine(_dir, "App", "obj", "App.csproj.reqnroll-ide.targets");
        File.WriteAllText(stub, MtpProjectStubs.BuildStubXml(BundleTargets)); // e.g. from an earlier build of the extension, or Reqnroll since removed
        var nuGetTargets = Path.Combine(_dir, "App", "obj", "App.csproj.nuget.g.targets");
        File.WriteAllText(nuGetTargets, "<Project />");

        MtpProjectStubs.TrySyncStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation).Should().Be(MtpStubSyncResult.NotReqnroll);

        File.Exists(stub).Should().BeFalse();
        File.Exists(nuGetTargets).Should().BeTrue("only our own stub is ever removed");
    }

    [Fact]
    public void TrySyncStub_leaves_a_project_that_has_not_been_restored_yet_alone()
    {
        var project = CreateProject(@"App\App.csproj");

        MtpProjectStubs.TrySyncStub(project, BundleTargets, _logger, ReadOrNull, NoEvaluation).Should().Be(MtpStubSyncResult.NotRestored);

        Directory.Exists(Path.Combine(_dir, "App", "obj")).Should().BeFalse("nothing is written until restore says whether the project uses Reqnroll");
    }

    [Fact]
    public void TrySyncStub_reads_the_restore_output_from_a_moved_obj_directory()
    {
        var project = CreateProject(@"App\App.csproj", @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><BaseIntermediateOutputPath>..\out\App\</BaseIntermediateOutputPath></PropertyGroup></Project>");
        var moved = CreateDir(@"out\App");
        File.WriteAllText(Path.Combine(moved, "project.assets.json"), Assets("Reqnroll.NUnit/3.3.4"));

        MtpProjectStubs.TrySyncStub(project, BundleTargets, _logger, ReadOrNull, _ => moved).Should().Be(MtpStubSyncResult.Written);

        File.Exists(Path.Combine(moved, "App.csproj.reqnroll-ide.targets")).Should().BeTrue();
        Directory.Exists(Path.Combine(_dir, "App", "obj")).Should().BeFalse();
    }

    [Fact]
    public void SyncStubs_puts_a_stub_only_in_the_Reqnroll_CSharp_projects_of_a_solution()
    {
        var specs = CreateRestoredProject(@"Specs\Specs.csproj", "Reqnroll.xunit.v3/3.3.3");
        var unitTests = CreateRestoredProject(@"UnitTests\UnitTests.csproj", "xunit.v3.mtp-v2/4.0.1");
        var app = CreateRestoredProject(@"App\App.csproj");
        var vbSpecs = CreateRestoredProject(@"VbSpecs\VbSpecs.vbproj", "Reqnroll.xunit.v3/3.3.3");

        MtpProjectStubs.SyncStubs(new List<string> { specs, unitTests, app, vbSpecs }, BundleTargets, _logger).Should().Be(1);

        Directory.GetFiles(_dir, "*.reqnroll-ide.targets", SearchOption.AllDirectories)
            .Should().ContainSingle().Which.Should().Be(Path.Combine(_dir, "Specs", "obj", "Specs.csproj.reqnroll-ide.targets"));
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
