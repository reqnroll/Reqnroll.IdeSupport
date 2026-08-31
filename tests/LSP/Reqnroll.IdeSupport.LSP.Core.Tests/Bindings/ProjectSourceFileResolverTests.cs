using System.IO;
using IoPath = System.IO.Path;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Covers the issue #540 resolution ladder: a source path recorded by discovery on the machine that
/// built the assembly, mapped (or not) onto a path that exists here.
/// </summary>
public class ProjectSourceFileResolverTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly string _projectFolder = IoPath.Combine(
        IoPath.GetTempPath(), "ProjectSourceFileResolverTests_" + Guid.NewGuid());

    public ProjectSourceFileResolverTests() => Directory.CreateDirectory(_projectFolder);

    public void Dispose()
    {
        try { Directory.Delete(_projectFolder, recursive: true); } catch { /* best effort */ }
    }

    private ProjectSourceFileResolver CreateSut() => new(_projectFolder, _fileSystem);

    /// <summary>Creates a file under the project folder and returns its full path.</summary>
    private string CreateFile(params string[] relativeSegments)
    {
        var fullPath = IoPath.Combine(new[] { _projectFolder }.Concat(relativeSegments).ToArray());
        Directory.CreateDirectory(IoPath.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "// test");
        return fullPath;
    }

    // ── Step 1: exact hit ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_path_that_exists_is_returned_unchanged()
    {
        var path = CreateFile("Support", "Hooks.cs");

        CreateSut().Resolve(path).Should().Be(path);
    }

    // ── Step 2: prefix remap ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_devcontainer_path_is_remapped_onto_the_project_folder()
    {
        // The exact shape from the 2026-08-31 incident: the Quickstart's assemblies were last built
        // inside a Linux devcontainer, so every binding's source path was /workspaces/... .
        var expected = CreateFile("Support", "PriceCalculationHooks.cs");

        var result = CreateSut().Resolve(
            "/workspaces/host-solution/ReqnrollQuickstart.Specs/Support/PriceCalculationHooks.cs");

        result.Should().Be(expected);
    }

    [Fact]
    public void A_deterministic_build_path_is_remapped_onto_the_project_folder()
    {
        // ContinuousIntegrationBuild=true rewrites source paths to /_/... .
        var expected = CreateFile("StepDefinitions", "CalculatorSteps.cs");

        CreateSut().Resolve("/_/StepDefinitions/CalculatorSteps.cs").Should().Be(expected);
    }

    [Fact]
    public void An_other_machine_windows_path_is_remapped_onto_the_project_folder()
    {
        var expected = CreateFile("StepDefinitions", "Steps.cs");

        CreateSut().Resolve(@"D:\build-agent\work\1\s\Specs\StepDefinitions\Steps.cs")
            .Should().Be(expected);
    }

    [Fact]
    public void The_longest_matching_suffix_wins()
    {
        // Two files share a name under different subtrees. Remapping must keep as much of the
        // recorded path as still exists here, so the more specific "Nested/Support/Hooks.cs" is
        // preferred over the shallower "Support/Hooks.cs".
        CreateFile("Support", "Hooks.cs");
        var deeper = CreateFile("Nested", "Support", "Hooks.cs");

        CreateSut().Resolve("/workspaces/host/Specs/Nested/Support/Hooks.cs").Should().Be(deeper);
    }

    // ── Step 3: unique name match ─────────────────────────────────────────────────────────

    [Fact]
    public void A_moved_file_falls_back_to_a_unique_name_match()
    {
        // The recorded directory structure no longer exists here (the file was moved since the
        // build), but exactly one file with that name does.
        var expected = CreateFile("NewLocation", "Steps.cs");

        CreateSut().Resolve("/workspaces/host/Specs/OldLocation/Steps.cs").Should().Be(expected);
    }

    [Fact]
    public void An_ambiguous_name_match_resolves_to_nothing_rather_than_guessing()
    {
        // A wrong navigation target is worse than none: it silently takes the user somewhere real
        // but incorrect, which is harder to diagnose than nothing happening.
        CreateFile("A", "Steps.cs");
        CreateFile("B", "Steps.cs");

        CreateSut().Resolve("/workspaces/host/Specs/Elsewhere/Steps.cs").Should().BeNull();
    }

    [Fact]
    public void Build_output_is_excluded_from_the_name_match()
    {
        // obj\Debug\...\Steps.cs would otherwise make every name match ambiguous, or resolve to a
        // generated copy rather than the source the user edits.
        var expected = CreateFile("StepDefinitions", "Steps.cs");
        CreateFile("obj", "Debug", "net8.0", "Steps.cs");
        CreateFile("bin", "Debug", "net8.0", "Steps.cs");

        CreateSut().Resolve("/workspaces/host/Specs/Gone/Steps.cs").Should().Be(expected);
    }

    // ── Containment ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_recorded_path_containing_parent_segments_cannot_escape_the_project_folder()
    {
        // The recorded path is whatever string a compiler baked into a PDB this process did not
        // produce. Re-rooting its segments onto the project folder must not honour "..", or
        // "navigate to this binding" becomes "open an arbitrary file elsewhere on disk".
        var outside = IoPath.Combine(IoPath.GetDirectoryName(_projectFolder)!, "Outside.cs");
        File.WriteAllText(outside, "// outside the project");
        try
        {
            CreateSut().Resolve("/workspaces/host/Specs/../../../Outside.cs").Should().BeNull();
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void An_inert_current_directory_segment_is_dropped_rather_than_failing_the_remap()
    {
        // Only ".." can escape the project folder. A "." segment carries no risk, so it must not
        // cost a binding its navigation target.
        var expected = CreateFile("Support", "Hooks.cs");

        CreateSut().Resolve("/workspaces/host/Specs/./Support/Hooks.cs").Should().Be(expected);
    }

    // ── Step 4: unresolved ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_path_with_no_local_counterpart_resolves_to_null()
    {
        CreateFile("Support", "Hooks.cs");

        CreateSut().Resolve("/workspaces/host/Specs/Support/SomethingElse.cs").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_recorded_path_resolves_to_null(string? recordedPath)
    {
        CreateSut().Resolve(recordedPath).Should().BeNull();
    }

    [Fact]
    public void Without_a_project_folder_only_an_exact_hit_resolves()
    {
        var path = CreateFile("Support", "Hooks.cs");
        var sut = new ProjectSourceFileResolver(projectFolder: null, _fileSystem);

        sut.Resolve(path).Should().Be(path);
        sut.Resolve("/workspaces/host/Specs/Support/Hooks.cs").Should().BeNull();
    }

    // ── Caching ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Repeated_queries_for_the_same_path_return_the_same_answer()
    {
        var expected = CreateFile("Support", "Hooks.cs");
        var sut = CreateSut();
        const string recorded = "/workspaces/host/Specs/Support/Hooks.cs";

        sut.Resolve(recorded).Should().Be(expected);
        sut.Resolve(recorded).Should().Be(expected);
        sut.Resolve("/workspaces/host/Specs/Support/Missing.cs").Should().BeNull();
        sut.Resolve("/workspaces/host/Specs/Support/Missing.cs").Should().BeNull();
    }
}

/// <summary>Covers the no-project-context resolver used by callers that only want an existence check.</summary>
public class LocalOnlySourceFileResolverTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly string _existingFile = IoPath.GetTempFileName();

    public void Dispose()
    {
        try { File.Delete(_existingFile); } catch { /* best effort */ }
    }

    [Fact]
    public void An_existing_path_resolves_to_itself()
    {
        new LocalOnlySourceFileResolver(_fileSystem).Resolve(_existingFile).Should().Be(_existingFile);
    }

    [Fact]
    public void A_foreign_path_is_never_remapped()
    {
        new LocalOnlySourceFileResolver(_fileSystem)
            .Resolve("/workspaces/host-solution/Steps.cs").Should().BeNull();
    }
}
