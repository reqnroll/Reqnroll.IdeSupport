namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>GherkinFormatConfiguration</summary>
public class GherkinFormatConfiguration
{
    /// <summary>
    ///     Specifies whether child elements of Feature (Background, Rule, Scenario, Scenario Outline) should be indented.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_feature_children")]
    public bool IndentFeatureChildren { get; set; }

    /// <summary>
    ///     Specifies whether child elements fo Rule (Background, Scenario, Scenario Outline) should be indented.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_rule_children")]
    public bool IndentRuleChildren { get; set; }

    /// <summary>
    ///     Specifies whether steps of scenarios should be indented.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_steps")]
    public bool IndentSteps { get; set; } = true;

    /// <summary>
    ///     Specifies whether the 'And' and 'But' steps of the scenarios should have an additional indentation.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_and_steps")]
    public bool IndentAndSteps { get; set; }

    /// <summary>
    ///     Specifies whether DataTable arguments should be indented within the step.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_datatable")]
    public bool IndentDataTable { get; set; } = true;

    /// <summary>
    ///     Specifies whether DocString arguments should be indented within the step.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_docstring")]
    public bool IndentDocString { get; set; } = true;

    /// <summary>
    ///     Specifies whether the Examples block should be indented within the Scenario Outline.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_examples")]
    public bool IndentExamples { get; set; }

    /// <summary>
    ///     Specifies whether the Examples table should be indented within the Examples block.
    /// </summary>
    [EditorConfigSetting("gherkin_indent_examples_table")]
    public bool IndentExamplesTable { get; set; } = true;

    private int _tableCellPaddingSize = 1;

    /// <summary>
    ///     The number of space characters to be used on each sides as table cell padding.
    ///     Negative values (e.g. from <c>.editorconfig</c>) are clamped to 0 when read: the formatter
    ///     builds the padding with <c>new string(' ', TableCellPaddingSize)</c>, which would otherwise
    ///     throw an <see cref="ArgumentOutOfRangeException"/> and break table formatting.
    /// </summary>
    [EditorConfigSetting("gherkin_table_cell_padding_size")]
    public int TableCellPaddingSize
    {
        get => _tableCellPaddingSize < 0 ? 0 : _tableCellPaddingSize;
        set => _tableCellPaddingSize = value;
    }

    /// <summary>
    ///     Right-align numeric table cells
    /// </summary>
    [EditorConfigSetting("gherkin_table_cell_right_align_numeric_content")]
    public bool TableCellRightAlignNumericContent { get; set; } = true;

    /// <summary>Creates a copy of this configuration.</summary>
    public GherkinFormatConfiguration Clone() => new()
    {
        IndentFeatureChildren           = IndentFeatureChildren,
        IndentRuleChildren              = IndentRuleChildren,
        IndentSteps                     = IndentSteps,
        IndentAndSteps                  = IndentAndSteps,
        IndentDataTable                 = IndentDataTable,
        IndentDocString                 = IndentDocString,
        IndentExamples                  = IndentExamples,
        IndentExamplesTable             = IndentExamplesTable,
        TableCellPaddingSize            = TableCellPaddingSize,
        TableCellRightAlignNumericContent = TableCellRightAlignNumericContent,
    };

    /// <summary>Validates this configuration. Currently a no-op; retained for symmetry with other configuration sections.</summary>
    public void CheckConfiguration()
    {
        // nop
    }

    #region Equality

    /// <summary>Determines whether this instance has the same setting values as <paramref name="other"/>.</summary>
    protected bool Equals(GherkinFormatConfiguration other) => IndentFeatureChildren == other.IndentFeatureChildren &&
                                                               IndentRuleChildren == other.IndentRuleChildren &&
                                                               IndentSteps == other.IndentSteps &&
                                                               IndentAndSteps == other.IndentAndSteps &&
                                                               IndentDataTable == other.IndentDataTable &&
                                                               IndentDocString == other.IndentDocString &&
                                                               IndentExamples == other.IndentExamples &&
                                                               IndentExamplesTable == other.IndentExamplesTable &&
                                                               TableCellPaddingSize == other.TableCellPaddingSize &&
                                                               TableCellRightAlignNumericContent == other.TableCellRightAlignNumericContent;

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="GherkinFormatConfiguration"/> with the same setting values.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((GherkinFormatConfiguration) obj);
    }

    // ReSharper disable NonReadonlyMemberInGetHashCode
    /// <summary>Returns a hash code derived from the configuration's setting values.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = IndentFeatureChildren.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentRuleChildren.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentSteps.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentAndSteps.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentDataTable.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentDocString.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentExamples.GetHashCode();
            hashCode = (hashCode * 397) ^ IndentExamplesTable.GetHashCode();
            hashCode = (hashCode * 397) ^ TableCellPaddingSize.GetHashCode();
            hashCode = (hashCode * 397) ^ TableCellRightAlignNumericContent.GetHashCode();
            return hashCode;
        }
    }
    // ReSharper restore NonReadonlyMemberInGetHashCode

    #endregion
}
