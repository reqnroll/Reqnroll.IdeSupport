using System.IO.Abstractions.TestingHelpers;
using Reqnroll.IdeSupport.Common.Configuration;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class FileSystemEditorConfigOptionsProviderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (FileSystemEditorConfigOptionsProvider Provider, MockFileSystem Fs)
        MakeProviderWithFs(Dictionary<string, string> editorConfigFiles)
    {
        var mockFiles = editorConfigFiles.ToDictionary(
            kv => kv.Key,
            kv => new MockFileData(kv.Value));
        var fs = new MockFileSystem(mockFiles);
        return (new FileSystemEditorConfigOptionsProvider(fs), fs);
    }

    private static string FilePath(params string[] parts) =>
        Path.Combine(parts);

    // ── Basic value reading ───────────────────────────────────────────────────

    [Fact]
    public void Returns_bool_value_from_matching_section()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Login.feature");

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = """
                   root = true
                   [*.feature]
                   gherkin_indent_steps = false
                   """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.False(opts.GetBoolOption("gherkin_indent_steps", true));
    }

    [Fact]
    public void Returns_int_value_from_matching_section()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Login.feature");

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = """
                   root = true
                   [*.feature]
                   gherkin_table_cell_padding_size = 3
                   """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.Equal(3, opts.GetOption("gherkin_table_cell_padding_size", 1));
    }

    [Fact]
    public void Returns_string_value_from_matching_section()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Foo.cs");

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = """
                   root = true
                   [*.cs]
                   csharp_style_namespace_declarations = file_scoped:suggestion
                   """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.Equal("file_scoped:suggestion",
            opts.GetOption<string?>("csharp_style_namespace_declarations", null));
    }

    // ── Non-matching sections ─────────────────────────────────────────────────

    [Fact]
    public void Does_not_apply_values_from_non_matching_section()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Login.feature");

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = """
                   root = true
                   [*.cs]
                   gherkin_indent_steps = false
                   """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.True(opts.GetBoolOption("gherkin_indent_steps", true)); // default preserved
    }

    // ── Upward search and root = true ─────────────────────────────────────────

    [Fact]
    public void Searches_upward_and_merges_parent_values()
    {
        var ecRoot  = @"C:\Repo\.editorconfig";
        var ecSub   = @"C:\Repo\src\Features\.editorconfig";
        var file    = @"C:\Repo\src\Features\Login.feature";

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ecRoot] = """
                       root = true
                       [*.feature]
                       gherkin_indent_steps = false
                       gherkin_table_cell_padding_size = 2
                       """,
            [ecSub]  = """
                       [*.feature]
                       gherkin_table_cell_padding_size = 4
                       """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);

        // Parent value, not overridden by sub
        Assert.False(opts.GetBoolOption("gherkin_indent_steps", true));
        // Sub overrides parent
        Assert.Equal(4, opts.GetOption("gherkin_table_cell_padding_size", 1));
    }

    [Fact]
    public void Stops_at_root_true_and_does_not_read_files_above()
    {
        var ecAbove   = @"C:\.editorconfig";
        var ecRepo    = @"C:\Repo\.editorconfig";
        var file      = @"C:\Repo\Login.feature";

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ecAbove] = """
                        [*.feature]
                        gherkin_indent_steps = false
                        """,
            [ecRepo]  = """
                        root = true
                        [*.feature]
                        gherkin_table_cell_padding_size = 5
                        """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);

        Assert.Equal(5, opts.GetOption("gherkin_table_cell_padding_size", 1));
        // Value from above the root = true barrier must NOT appear
        Assert.True(opts.GetBoolOption("gherkin_indent_steps", true));
    }

    // ── Default when key absent ───────────────────────────────────────────────

    [Fact]
    public void Returns_default_value_when_key_not_in_editorconfig()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Login.feature");

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = "root = true\n[*.feature]\ngherkin_indent_feature_children = true\n"
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.Equal(1, opts.GetOption("gherkin_table_cell_padding_size", 1));
    }

    [Fact]
    public void Returns_NullEditorConfigOptions_behaviour_when_no_editorconfig_present()
    {
        var (sut, _) = MakeProviderWithFs(new());

        var opts = sut.GetEditorConfigOptionsByPath(Path.Combine(Path.GetTempPath(), "Login.feature"));
        Assert.True(opts.GetBoolOption("gherkin_indent_steps", true));
    }

    // ── Section glob matching ──────────────────────────────────────────────────

    [Fact]
    public void Wildcard_star_pattern_matches_file_in_subdirectory()
    {
        // [*.feature] without a / should match recursively per EditorConfig convention
        var ec   = @"C:\Repo\.editorconfig";
        var file = @"C:\Repo\src\Features\Login.feature";

        var (sut, _) = MakeProviderWithFs(new()
        {
            [ec] = """
                   root = true
                   [*.feature]
                   gherkin_indent_steps = false
                   """
        });

        var opts = sut.GetEditorConfigOptionsByPath(file);
        Assert.False(opts.GetBoolOption("gherkin_indent_steps", true));
    }

    // ── Cache invalidation ────────────────────────────────────────────────────

    [Fact]
    public void InvalidateCache_causes_rereading_on_next_call()
    {
        var root = Path.GetTempPath();
        var ec   = Path.Combine(root, ".editorconfig");
        var file = Path.Combine(root, "Login.feature");
        var (sut, fs) = MakeProviderWithFs(new()
        {
            [ec] = "root = true\n[*.feature]\ngherkin_indent_steps = false\n"
        });

        // First read — value comes from initial content
        var opts1 = sut.GetEditorConfigOptionsByPath(file);
        Assert.False(opts1.GetBoolOption("gherkin_indent_steps", true));

        // Update the mock file content and move its timestamp forward
        fs.File.WriteAllText(ec, "root = true\n[*.feature]\ngherkin_indent_steps = true\n");
        fs.File.SetLastWriteTimeUtc(ec, DateTime.UtcNow.AddSeconds(1));

        sut.InvalidateCache(ec);

        // Second read — must pick up the updated content
        var opts2 = sut.GetEditorConfigOptionsByPath(file);
        Assert.True(opts2.GetBoolOption("gherkin_indent_steps", false));
    }
}
