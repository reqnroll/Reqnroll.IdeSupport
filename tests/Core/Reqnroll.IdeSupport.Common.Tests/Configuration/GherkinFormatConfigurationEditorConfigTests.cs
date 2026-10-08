using Reqnroll.IdeSupport.Common.Configuration;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

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

public class GherkinFormatConfigurationEditorConfigTests
{
    private static GherkinFormatConfiguration ApplyOptions(Dictionary<string, string> values)
    {
        var config  = new GherkinFormatConfiguration();
        var options = new DictEditorConfigOptions(values);
        options.UpdateFromEditorConfig(config);
        return config;
    }

    [Theory]
    [InlineData("gherkin_indent_feature_children", true,  nameof(GherkinFormatConfiguration.IndentFeatureChildren))]
    [InlineData("gherkin_indent_rule_children",    true,  nameof(GherkinFormatConfiguration.IndentRuleChildren))]
    [InlineData("gherkin_indent_steps",            false, nameof(GherkinFormatConfiguration.IndentSteps))]
    [InlineData("gherkin_indent_and_steps",        true,  nameof(GherkinFormatConfiguration.IndentAndSteps))]
    [InlineData("gherkin_indent_datatable",        false, nameof(GherkinFormatConfiguration.IndentDataTable))]
    [InlineData("gherkin_indent_docstring",        false, nameof(GherkinFormatConfiguration.IndentDocString))]
    [InlineData("gherkin_indent_examples",         true,  nameof(GherkinFormatConfiguration.IndentExamples))]
    [InlineData("gherkin_indent_examples_table",   false, nameof(GherkinFormatConfiguration.IndentExamplesTable))]
    [InlineData("gherkin_table_cell_right_align_numeric_content", false,
        nameof(GherkinFormatConfiguration.TableCellRightAlignNumericContent))]
    public void UpdateFromEditorConfig_sets_bool_property(string key, bool value, string propertyName)
    {
        var config = ApplyOptions(new() { [key] = value ? "true" : "false" });
        var prop   = typeof(GherkinFormatConfiguration).GetProperty(propertyName)!;

        Assert.Equal(value, (bool)prop.GetValue(config)!);
    }

    [Fact]
    public void UpdateFromEditorConfig_sets_table_cell_padding_size()
    {
        var config = ApplyOptions(new() { ["gherkin_table_cell_padding_size"] = "2" });
        Assert.Equal(2, config.TableCellPaddingSize);
    }

    [Fact]
    public void UpdateFromEditorConfig_does_not_mutate_source_config()
    {
        var original = new GherkinFormatConfiguration(); // all defaults
        var clone    = original.Clone();

        // Apply an override to the clone only
        var options = new DictEditorConfigOptions(new() { ["gherkin_indent_steps"] = "false" });
        options.UpdateFromEditorConfig(clone);

        Assert.True(original.IndentSteps,  "original must be unchanged");
        Assert.False(clone.IndentSteps,    "clone must reflect the override");
    }

    [Fact]
    public void UpdateFromEditorConfig_negative_table_cell_padding_size_does_not_break_formatter_padding()
    {
        var config = ApplyOptions(new() { ["gherkin_table_cell_padding_size"] = "-1" });

        // GherkinFormatSettings builds the table cell padding as `new string(' ', TableCellPaddingSize)`,
        // which throws ArgumentOutOfRangeException for a negative size.
        var padding = new string(' ', config.TableCellPaddingSize);

        Assert.True(config.TableCellPaddingSize >= 0,
            "a negative gherkin_table_cell_padding_size must be clamped to a non-negative value");
        Assert.Empty(padding);
    }

    [Fact]
    public void TableCellPaddingSize_clamps_negative_value_to_zero()
    {
        var config = new GherkinFormatConfiguration { TableCellPaddingSize = -5 };

        Assert.Equal(0, config.TableCellPaddingSize);
    }
}
