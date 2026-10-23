using Reqnroll.IdeSupport.Common.Configuration.EditorConfig;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class EditorConfigOptionsExtensionsTests
{
    // ── bool property ─────────────────────────────────────────────────────────

    [Fact]
    public void UpdateFromEditorConfig_sets_bool_property_when_key_present()
    {
        var config  = new GherkinFormatConfiguration(); // IndentSteps defaults to true
        var options = new DictEditorConfigOptions(new() { ["gherkin_indent_steps"] = "false" });

        options.UpdateFromEditorConfig(config);

        Assert.False(config.IndentSteps);
    }

    [Fact]
    public void UpdateFromEditorConfig_keeps_bool_default_when_key_absent()
    {
        var config  = new GherkinFormatConfiguration(); // IndentSteps = true
        var options = new DictEditorConfigOptions(new());

        options.UpdateFromEditorConfig(config);

        Assert.True(config.IndentSteps);
    }

    // ── int property ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateFromEditorConfig_sets_int_property_when_key_present()
    {
        var config  = new GherkinFormatConfiguration(); // TableCellPaddingSize = 1
        var options = new DictEditorConfigOptions(new() { ["gherkin_table_cell_padding_size"] = "3" });

        options.UpdateFromEditorConfig(config);

        Assert.Equal(3, config.TableCellPaddingSize);
    }

    [Fact]
    public void UpdateFromEditorConfig_keeps_int_default_when_key_absent()
    {
        var config  = new GherkinFormatConfiguration(); // TableCellPaddingSize = 1
        var options = new DictEditorConfigOptions(new());

        options.UpdateFromEditorConfig(config);

        Assert.Equal(1, config.TableCellPaddingSize);
    }

    // ── string property ───────────────────────────────────────────────────────

    [Fact]
    public void UpdateFromEditorConfig_sets_string_property_when_key_present()
    {
        var config  = new CSharpCodeGenerationConfiguration(); // default = "block_scoped"
        var options = new DictEditorConfigOptions(
            new() { ["csharp_style_namespace_declarations"] = "file_scoped:suggestion" });

        options.UpdateFromEditorConfig(config);

        Assert.Equal("file_scoped:suggestion", config.NamespaceDeclarationStyle);
    }

    [Fact]
    public void UpdateFromEditorConfig_keeps_string_default_when_key_absent()
    {
        var config  = new CSharpCodeGenerationConfiguration();
        var options = new DictEditorConfigOptions(new());

        options.UpdateFromEditorConfig(config);

        Assert.Equal("block_scoped", config.NamespaceDeclarationStyle);
    }

    // ── unknown keys ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateFromEditorConfig_ignores_unknown_keys_gracefully()
    {
        var config  = new GherkinFormatConfiguration();
        var options = new DictEditorConfigOptions(new() { ["unknown_key"] = "value" });

        var act = () => options.UpdateFromEditorConfig(config);

        act.Should().NotThrow();
    }
}

/// <summary>Simple IEditorConfigOptions backed by a dictionary, for use in tests.</summary>
file sealed class DictEditorConfigOptions : IEditorConfigOptions
{
    private readonly Dictionary<string, string> _values;

    public DictEditorConfigOptions(Dictionary<string, string> values) => _values = values;

    public TResult GetOption<TResult>(string key, TResult defaultValue)
    {
        if (!_values.TryGetValue(key, out var raw)) return defaultValue;
        if (typeof(TResult) == typeof(bool))
            return (TResult)(object)(raw.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (typeof(TResult) == typeof(int) && int.TryParse(raw, out var i))
            return (TResult)(object)i;
        if (typeof(TResult) == typeof(string))
            return (TResult)(object)raw;
        return defaultValue;
    }

    public bool GetBoolOption(string key, bool defaultValue) => GetOption(key, defaultValue);
}
