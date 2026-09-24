namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests.Injection;

/// <summary>
/// Issue #741: the project-local stub + <c>Reqnroll.IdeSupport.TestReporter.MTP.targets</c> + generated
/// reporter sources, exercised with real <c>dotnet build</c>s of throwaway user projects. Every project
/// is built exactly as a user's would be (outside the repo, no repo props), with nothing but the
/// <c>obj/</c> stub connecting it to the reporter.
/// </summary>
[Trait("Category", "Integration")]
public class SourceInjectionBuildTests
{
    private const string LatestMsTest = "4.2.3";

    /// <summary>The runtime package alone: enough for the .targets file's Reqnroll gate (a resolved Reqnroll.dll) without Reqnroll's code generation.</summary>
    private const string ReqnrollReference = """<PackageReference Include="Reqnroll" Version="3.3.4" />""";

    /// <summary>An MTP-only MSTest project (no Microsoft.NET.Test.Sdk, so IsTestProject is never set) that references Reqnroll — the shape issue #741's spike found main's IsTestProject guard wrongly excluded.</summary>
    private static string MsTestMtpProject(string msTestVersion = LatestMsTest, string extraProperties = "", string extraItems = "", bool reqnroll = true) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <EnableMSTestRunner>true</EnableMSTestRunner>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            {extraProperties}
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="MSTest.TestAdapter" Version="{msTestVersion}" />
            <PackageReference Include="MSTest.TestFramework" Version="{msTestVersion}" />
            {(reqnroll ? ReqnrollReference : "")}
            {extraItems}
          </ItemGroup>
        </Project>
        """;

    private static DotnetResult Build(InjectionWorkspace workspace, params string[] extra) =>
        workspace.Dotnet(["build", "-nologo", "-nodeReuse:false", .. extra]);

    private static void ShouldSucceed(DotnetResult result) =>
        result.ExitCode.Should().Be(0, "the build must succeed; output:\n" + result.Output);

    // ---- T1: compile + register ------------------------------------------------------------

    [Fact]
    public void An_MTP_only_project_gets_the_sources_compiled_in_and_the_hook_registered_with_no_reporter_binary()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject());

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeTrue(workspace.SelfRegisteredExtensions());
        workspace.InjectedSources().Should().BeEquivalentTo(
            "NdjsonWriter.cs", "OutcomeSink.cs", "ReqnrollMtpReporter.cs", "SessionBreadcrumbMatcher.cs",
            "SessionsDirectory.cs", "TestingPlatformBuilderHook.cs", "WorkspaceRootLocator.cs");
        Directory.GetFiles(workspace.OutputDirectory(), "Reqnroll.IdeSupport.*").Should().BeEmpty("the reporter is compiled from source; no assembly of ours is shipped into bin/");
    }

    // ---- T2/T10: the supported Microsoft.Testing.Platform range --------------------------

    [Theory]
    [InlineData("4.0.0", "")]   // Microsoft.Testing.Platform 2.0.0: the floor, the REQNROLL_IDE_MTP_BEFORE_2_1 branch.
    [InlineData("4.1.0", "")]   // 2.1.0: first version with AddTestSessionLifetimeHandler (old spelling is a CS0619 error).
    [InlineData("4.2.3", "")]   // 2.2.3
    [InlineData("4.2.3", "2.4.0")] // A newer MTP floated above what MSTest itself asks for.
    public void Every_supported_platform_version_compiles_the_sources_and_registers_the_hook(string msTestVersion, string platformOverride)
    {
        var extraItems = platformOverride == "" ? "" : $"""<PackageReference Include="Microsoft.Testing.Platform" Version="{platformOverride}" />""";
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(msTestVersion, extraItems: extraItems));

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeTrue();
    }

    /// <summary>
    /// Issue #741 T9: the latest published MSTest + Microsoft.Testing.Platform. Run weekly by
    /// .github/workflows/mtp-reporter-latest.yml; skipped otherwise, because its result depends on
    /// nuget.org rather than on the commit. Fails when a new release breaks the injected sources at
    /// compile time, or ships a major outside the range the .targets file gates on.
    /// </summary>
    [LatestPublishedFact]
    public void The_LatestPublished_MSTest_and_platform_still_compile_the_sources_and_register_the_hook()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject("*", extraItems: """<PackageReference Include="Microsoft.Testing.Platform" Version="*" />"""));

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        var reqnrollMessages = string.Join("\n", result.Output.Split('\n')
            .Where(l => l.Contains("Reqnroll IDE Support:", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .Distinct());
        workspace.ReporterHookRegistered().Should().BeTrue(
            "the reporter must still compile into a project on the latest releases; if a new major shipped, revisit the supported range in Reqnroll.IdeSupport.TestReporter.MTP.targets. Reqnroll build messages:\n" + reqnrollMessages);
    }

    [Fact]
    public void A_platform_version_outside_the_supported_range_is_skipped_with_a_message_not_broken()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject("3.11.1")); // Microsoft.Testing.Platform 1.9.1

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        workspace.ReporterHookRegistered().Should().BeFalse();
        result.Output.Should().Contain("Microsoft.Testing.Platform 1.9.1 is outside the supported range [2.0.0, 3.0.0)");
    }

    // ---- T3: hostile compiler settings -----------------------------------------------------

    [Fact]
    public void The_injected_sources_build_clean_under_the_strictest_compiler_and_analyzer_settings()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(extraProperties: """
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <WarningsAsErrors>$(WarningsAsErrors);CS0618;CS0612;nullable</WarningsAsErrors>
            <AnalysisLevel>latest-all</AnalysisLevel>
            <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
            <LangVersion>12</LangVersion>
            """).Replace("<ImplicitUsings>enable</ImplicitUsings>", "<ImplicitUsings>disable</ImplicitUsings>"));

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeTrue();
    }

    // ---- T4 + T12: incremental build, clean, and removing the stub -------------------------

    [Fact]
    public void A_no_change_rebuild_does_not_recompile_clean_keeps_the_stub_and_removing_the_stub_removes_the_reporter()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject());
        ShouldSucceed(Build(workspace));
        var assembly = Path.Combine(workspace.IntermediateDirectory(), "Mtp.dll");
        var firstWrite = File.GetLastWriteTimeUtc(assembly);

        ShouldSucceed(Build(workspace));
        File.GetLastWriteTimeUtc(assembly).Should().Be(firstWrite, "an unchanged project must not be recompiled because of the injection");

        ShouldSucceed(workspace.Dotnet("clean", "-nologo", "-nodeReuse:false"));
        workspace.InjectedSources().Should().BeEmpty("the copied sources are FileWrites, so clean removes them");
        File.Exists(workspace.StubPath).Should().BeTrue("the stub is not a build output; clean must leave it for the next build");

        File.Delete(workspace.StubPath);
        ShouldSucceed(Build(workspace));
        workspace.ReporterHookRegistered().Should().BeFalse("with no stub, nothing of ours is imported");
        workspace.InjectedSources().Should().BeEmpty();
    }

    // ---- T5: negative gating ---------------------------------------------------------------

    [Fact]
    public void An_MTP_test_project_that_does_not_use_Reqnroll_gets_nothing_even_with_a_stub()
    {
        using var workspace = InjectionWorkspace.Create("PlainMtp.csproj", MsTestMtpProject(reqnroll: false));

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        workspace.ReporterHookRegistered().Should().BeFalse();
        result.Output.Should().Contain("it does not reference Reqnroll");
    }

    [Fact]
    public void A_VB_test_project_is_skipped()
    {
        using var workspace = InjectionWorkspace.Create("VbMtp.vbproj", MsTestMtpProject()
            .Replace("<Nullable>enable</Nullable>", "")
            .Replace("<ImplicitUsings>enable</ImplicitUsings>", ""));

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        workspace.ReporterHookRegistered().Should().BeFalse();
        result.Output.Should().Contain("the project language is 'VB'");
    }

    [Fact]
    public void A_class_library_that_merely_references_the_platform_is_skipped()
    {
        using var workspace = InjectionWorkspace.Create("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Testing.Platform" Version="2.2.3" />
                <PackageReference Include="Reqnroll" Version="3.3.4" />
              </ItemGroup>
            </Project>
            """);

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        result.Output.Should().Contain("it is not a test host");
    }

    [Fact]
    public void A_VSTest_mode_test_project_is_skipped()
    {
        using var workspace = InjectionWorkspace.Create("VsTest.csproj", MsTestMtpProject(extraItems: """<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.6.0" />""")
            .Replace("<OutputType>Exe</OutputType>", "")
            .Replace("<EnableMSTestRunner>true</EnableMSTestRunner>", ""));

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        result.Output.Should().Contain("it does not run on Microsoft.Testing.Platform");
    }

    [Theory]
    [InlineData("<ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>", "ReqnrollIdeSupportDisableMtpReporter is true")]
    [InlineData("<LangVersion>11</LangVersion>", "LangVersion 11 is older than C# 12.0")]
    public void An_explicit_opt_out_or_too_old_a_language_version_is_skipped(string property, string expectedReason)
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(extraProperties: property));

        var result = Build(workspace, "-v:d");

        ShouldSucceed(result);
        workspace.InjectedSources().Should().BeEmpty();
        result.Output.Should().Contain(expectedReason);
    }

    [Fact]
    public void A_stub_whose_bundle_no_longer_exists_is_inert()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(), writeStub: false);
        workspace.WriteStub(Path.Combine(workspace.Root, "uninstalled-extension", "Reqnroll.IdeSupport.TestReporter.MTP.targets"));

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeFalse();
        workspace.InjectedSources().Should().BeEmpty();
    }

    // ---- Fresh-eyes findings ----------------------------------------------------------------

    [Fact]
    public void A_bundle_under_a_path_with_quote_ampersand_dollar_and_percent_still_imports()
    {
        // The IDE bundle lives under the user profile, so the user name is part of the path. Before the
        // stub escaped it and moved it out of the Exists('...') literal, a name like O'Brien broke every
        // build of the project (MSB4024).
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(), writeStub: false);
        var odd = Path.Combine(workspace.Root, "O'Brien & $Co 100%");
        Directory.CreateDirectory(Path.Combine(odd, "ReporterSource"));
        File.Copy(InjectionWorkspace.BundleTargetsPath, Path.Combine(odd, Path.GetFileName(InjectionWorkspace.BundleTargetsPath)));
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ReporterSource")))
            File.Copy(source, Path.Combine(odd, "ReporterSource", Path.GetFileName(source)));
        workspace.WriteStub(Path.Combine(odd, Path.GetFileName(InjectionWorkspace.BundleTargetsPath)));

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeTrue();
    }

    [Fact]
    public void A_leftover_prebuilt_reporter_reference_and_hook_item_are_superseded_not_duplicated()
    {
        // What the per-user ImportAfter file earlier extension versions wrote (and another VS install may
        // still write): a HintPath reference to the prebuilt DLL plus the same hook item.
        var dll = Path.Combine(AppContext.BaseDirectory, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(
            extraProperties: "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
            extraItems: $"""
                <Reference Include="Reqnroll.IdeSupport.TestReporter.MTP"><HintPath>{dll}</HintPath></Reference>
                <TestingPlatformBuilderHook Include="a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c">
                  <DisplayName>Reqnroll.IdeSupport.TestReporter.MTP</DisplayName>
                  <TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>
                </TestingPlatformBuilderHook>
                """));

        ShouldSucceed(Build(workspace));

        var registrations = workspace.SelfRegisteredExtensions().Split("Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook.AddExtensions").Length - 1;
        registrations.Should().Be(1, "the hook must be registered exactly once");
        Directory.GetFiles(workspace.OutputDirectory(), "Reqnroll.IdeSupport.*").Should().BeEmpty("the stale prebuilt reference is superseded by the compiled sources");
    }

    // ---- T6: collisions with user types and global usings -----------------------------------

    [Fact]
    public void User_types_and_global_usings_that_reuse_the_reporters_names_do_not_break_the_build()
    {
        using var workspace = InjectionWorkspace.Create("Mtp.csproj", MsTestMtpProject(), new Dictionary<string, string>
        {
            ["Collide.cs"] = """
                global using Collide;

                namespace Collide
                {
                    public class Path { }
                    public class OutcomeSink { }
                    public class NdjsonWriter { }
                    public class WorkspaceRootLocator { }
                }

                namespace Mtp
                {
                    public static class TestingPlatformBuilderHook { }
                }
                """,
        });

        ShouldSucceed(Build(workspace));

        workspace.ReporterHookRegistered().Should().BeTrue();
    }
}

/// <summary>A <see cref="FactAttribute"/> that only runs when <c>REQNROLL_MTP_TEST_LATEST=true</c> (the scheduled mtp-reporter-latest workflow).</summary>
internal sealed class LatestPublishedFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "REQNROLL_MTP_TEST_LATEST";

    public LatestPublishedFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase))
            Skip = $"Depends on nuget.org, not on the commit: set {EnvironmentVariable}=true to run (.github/workflows/mtp-reporter-latest.yml).";
    }
}
