namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// String constants identifying the kind of a <c>IdeSupportTag</c> node produced while walking a
/// parsed feature document (e.g. by <c>IdeSupportTagParser</c>) — used to classify tags for
/// semantic tokens, diagnostics, and binding-match lookup.
/// </summary>
public static class IdeSupportTagTypes
{
    /// <summary>Tag type for a Feature block.</summary>
    public const string FeatureBlock = nameof(FeatureBlock);
    /// <summary>Tag type for a Rule block.</summary>
    public const string RuleBlock = nameof(RuleBlock);
    /// <summary>Tag type for a Scenario, Scenario Outline, or Background block.</summary>
    public const string ScenarioDefinitionBlock = nameof(ScenarioDefinitionBlock);
    /// <summary>Tag type for a reference to a hook that applies to a scenario.</summary>
    public const string ScenarioHookReference = nameof(ScenarioHookReference);
    /// <summary>Tag type for a single Gherkin step line.</summary>
    public const string StepBlock = nameof(StepBlock);
    /// <summary>Tag type for an Examples block within a Scenario Outline.</summary>
    public const string ExamplesBlock = nameof(ExamplesBlock);
    /// <summary>Tag type for a step's leading keyword (Given/When/Then/And/But).</summary>
    public const string StepKeyword = nameof(StepKeyword);
    /// <summary>Tag type for the keyword introducing a Feature/Rule/Scenario/Examples definition line.</summary>
    public const string DefinitionLineKeyword = nameof(DefinitionLineKeyword);
    /// <summary>Tag type for a step with no matching binding.</summary>
    public const string UndefinedStep = nameof(UndefinedStep);
    /// <summary>Tag type for a step that resolves to exactly one binding.</summary>
    public const string DefinedStep = nameof(DefinedStep);
    /// <summary>Tag type for a step that matches more than one binding.</summary>
    public const string AmbiguousStep = nameof(AmbiguousStep);
    /// <summary>Tag type for a parameter placeholder within a step's text.</summary>
    public const string StepParameter = nameof(StepParameter);
    /// <summary>Tag type for a <c>&lt;placeholder&gt;</c> token in a Scenario Outline step, substituted from the Examples table.</summary>
    public const string ScenarioOutlinePlaceholder = nameof(ScenarioOutlinePlaceholder);
    /// <summary>Tag type for a step-binding error (e.g. a binding resolution failure) distinct from a parser syntax error.</summary>
    public const string BindingError = nameof(BindingError);
    /// <summary>Tag type for a data table attached to a step.</summary>
    public const string DataTable = nameof(DataTable);
    /// <summary>Tag type for an <c>@tag</c> annotation on a feature, scenario, or examples block.</summary>
    public const string Tag = nameof(Tag);
    /// <summary>Tag type for the free-text description under a Feature/Rule/Scenario/Examples header.</summary>
    public const string Description = nameof(Description);
    /// <summary>Tag type for a <c>#</c> comment line.</summary>
    public const string Comment = nameof(Comment);
    /// <summary>Tag type for a multi-line doc-string (<c>"""</c> or <c>```</c>) attached to a step.</summary>
    public const string DocString = nameof(DocString);
    /// <summary>Tag type for a Gherkin syntax parser error.</summary>
    public const string ParserError = nameof(ParserError);
    /// <summary>Tag type for the root node representing the whole parsed document.</summary>
    public const string Document = nameof(Document);
    /// <summary>Tag type for the header row of a data table.</summary>
    public const string DataTableHeader = nameof(DataTableHeader);
}
