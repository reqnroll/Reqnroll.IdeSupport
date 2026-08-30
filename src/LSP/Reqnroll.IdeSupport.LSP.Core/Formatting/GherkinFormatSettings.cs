using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

namespace Reqnroll.IdeSupport.LSP.Core.Formatting;

/// <summary>
/// Resolved formatting settings for a single document format pass.
/// </summary>
public class GherkinFormatSettings
{
    /// <summary>Gets or sets the configuration.</summary>
    public GherkinFormatConfiguration Configuration { get; set; } = new();

    /// <summary>Gets or sets the indent.</summary>
    public string Indent { get; set; } = "    ";

    /// <summary>Gets the indent level for feature children (0 or 1), based on <see cref="Configuration"/>.</summary>
    public int FeatureChildrenIndentLevel => Configuration.IndentFeatureChildren ? 1 : 0;
    /// <summary>Gets the indent level for rule children relative to their enclosing rule (0 or 1).</summary>
    public int RuleChildrenIndentLevelWithinRule => Configuration.IndentRuleChildren ? 1 : 0;
    /// <summary>Gets the indent level for steps relative to their enclosing step container (0 or 1).</summary>
    public int StepIndentLevelWithinStepContainer => Configuration.IndentSteps ? 1 : 0;
    /// <summary>Gets the additional indent level for "And"/"But" steps relative to other steps (0 or 1).</summary>
    public int AndStepIndentLevelWithinSteps => Configuration.IndentAndSteps ? 1 : 0;
    /// <summary>Gets the indent level for a data table relative to its enclosing step (0 or 1).</summary>
    public int DataTableIndentLevelWithinStep => Configuration.IndentDataTable ? 1 : 0;
    /// <summary>Gets the indent level for a doc string relative to its enclosing step (0 or 1).</summary>
    public int DocStringIndentLevelWithinStep => Configuration.IndentDocString ? 1 : 0;
    /// <summary>Gets the indent level for an examples block relative to its enclosing scenario outline (0 or 1).</summary>
    public int ExamplesBlockIndentLevelWithinScenarioOutline => Configuration.IndentExamples ? 1 : 0;
    /// <summary>Gets the indent level for the examples table relative to its enclosing examples block (0 or 1).</summary>
    public int ExamplesTableIndentLevelWithinExamplesBlock => Configuration.IndentExamplesTable ? 1 : 0;
    /// <summary>Gets the whitespace padding inserted around each table cell's content.</summary>
    public string TableCellPadding => new(' ', Configuration.TableCellPaddingSize);
    /// <summary>Gets whether numeric table cell content should be right-aligned.</summary>
    public bool RightAlignNumericTableCells => Configuration.TableCellRightAlignNumericContent;

    /// <summary>
    /// Builds format settings from LSP <c>FormattingOptions</c>, applying editorconfig overrides
    /// and any Reqnroll-specific configuration from <paramref name="configuration"/>.
    /// </summary>
    public static GherkinFormatSettings FromLspOptions(
        int tabSize, bool insertSpaces,
        IEditorConfigOptionsProvider editorConfigOptionsProvider,
        string filePath,
        IdeSupportConfiguration? configuration)
    {
        var gherkinFormatConfig = configuration?.Editor?.GherkinFormat?.Clone() ?? new GherkinFormatConfiguration();

        var editorConfigOptions = editorConfigOptionsProvider.GetEditorConfigOptionsByPath(filePath);
        editorConfigOptions.UpdateFromEditorConfig(gherkinFormatConfig);

        var indent = insertSpaces ? new string(' ', tabSize) : "\t";

        return new GherkinFormatSettings
        {
            Indent = indent,
            Configuration = gherkinFormatConfig
        };
    }
}
