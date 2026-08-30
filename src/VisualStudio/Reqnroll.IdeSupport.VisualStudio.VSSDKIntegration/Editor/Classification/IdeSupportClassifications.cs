#nullable disable
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Classification;

namespace Reqnroll.IdeSupport.VisualStudio.Editor.Classification;

/// <summary>
/// MEF exports that register the custom Reqnroll classification types and their default
/// editor formats (colours, styles) in Visual Studio.
/// </summary>
/// <remarks>
/// <para>
/// The classification type names are the shared <see cref="ReqnrollClassificationTypeNames"/>
/// values — exactly the names the LSP server advertises in its semantic-token legend.  Because
/// Visual Studio's LSP client resolves a semantic token type to a classification type of the
/// same name, registering these classification types (plus their <see cref="ClassificationFormatDefinition"/>
/// formats) makes the LSP-driven colouring identical to the legacy <c>Reqnroll.VisualStudio</c>
/// extension — existing users keep their configured Reqnroll colours under
/// Tools → Options → Fonts and Colors with no migration.
/// </para>
/// <para>
/// This is the "VSSDK side" of the new extension: the LSP coloring pipeline replaces the legacy
/// classifier/tagger, but the classification <i>definitions</i> and their default formats are
/// carried forward verbatim for continuity.  The <c>.feature</c> content type itself is now
/// registered via VisualStudio.Extensibility, so the legacy content-type / file-extension
/// exports are intentionally not duplicated here.
/// </para>
/// </remarks>
internal static class IdeSupportClassifications
{
    public const string Keyword = ReqnrollClassificationTypeNames.Keyword;
    public const string Tag = ReqnrollClassificationTypeNames.Tag;
    public const string Description = ReqnrollClassificationTypeNames.Description;
    public const string Comment = ReqnrollClassificationTypeNames.Comment;
    public const string DocString = ReqnrollClassificationTypeNames.DocString;
    public const string DataTable = ReqnrollClassificationTypeNames.DataTable;
    public const string DataTableHeader = ReqnrollClassificationTypeNames.DataTableHeader;

    public const string UndefinedStep = ReqnrollClassificationTypeNames.UndefinedStep;
    public const string AmbiguousStep = ReqnrollClassificationTypeNames.AmbiguousStep;
    public const string StepParameter = ReqnrollClassificationTypeNames.StepParameter;
    public const string ScenarioOutlinePlaceholder = ReqnrollClassificationTypeNames.ScenarioOutlinePlaceholder;

    // This disables "The field is never used" compiler's warning. Justification: the field is used by MEF.
#pragma warning disable 169

    [Export] [Name(Keyword)] [BaseDefinition("keyword")]
    private static ClassificationTypeDefinition _keywordClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = Keyword)]
    [Name(Keyword)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinKeywordClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinKeywordClassificationFormat()
        {
            DisplayName = "Reqnroll Keyword";
        }
    }


    [Export] [Name(Tag)] [BaseDefinition("type")]
    private static ClassificationTypeDefinition _tagClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = Tag)]
    [Name(Tag)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinTagClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinTagClassificationFormat()
        {
            DisplayName = "Reqnroll Tag";
        }
    }


    [Export] [Name(Description)] [BaseDefinition("excluded code")]
    private static ClassificationTypeDefinition _descriptionClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = Description)]
    [Name(Description)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinDescriptionClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinDescriptionClassificationFormat()
        {
            DisplayName = "Reqnroll Description";
            IsItalic = true;
        }
    }


    [Export] [Name(DocString)] [BaseDefinition("string")]
    private static ClassificationTypeDefinition _docStringClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = DocString)]
    [Name(DocString)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinDocStringClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinDocStringClassificationFormat()
        {
            DisplayName = "Reqnroll Doc String";
        }
    }


    [Export] [Name(DataTable)] [BaseDefinition("string")]
    private static ClassificationTypeDefinition _dataTableClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = DataTable)]
    [Name(DataTable)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinDataTableClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinDataTableClassificationFormat()
        {
            DisplayName = "Reqnroll Data Table";
        }
    }


    [Export] [Name(DataTableHeader)] [BaseDefinition(DataTable)]
    private static ClassificationTypeDefinition _dataTableHeaderClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = DataTableHeader)]
    [Name(DataTableHeader)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinDataTableHeaderClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinDataTableHeaderClassificationFormat()
        {
            DisplayName = "Reqnroll Data Table Header";
            IsItalic = true;
        }
    }


    [Export] [Name(Comment)] [BaseDefinition("comment")]
    private static ClassificationTypeDefinition _commentClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = Comment)]
    [Name(Comment)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinCommentClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinCommentClassificationFormat()
        {
            DisplayName = "Reqnroll Comment";
        }
    }


    [Export] [Name(AmbiguousStep)]
    private static ClassificationTypeDefinition _ambiguousStepClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = AmbiguousStep)]
    [Name(AmbiguousStep)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinAmbiguousStepClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinAmbiguousStepClassificationFormat()
        {
            DisplayName = "Reqnroll Ambiguous Step";
            ForegroundColor = (Color) ColorConverter.ConvertFromString("#FF8C00"); // dark orange
        }
    }


    [Export] [Name(UndefinedStep)]
    private static ClassificationTypeDefinition _undefinedStepClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UndefinedStep)]
    [Name(UndefinedStep)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinUndefinedStepClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinUndefinedStepClassificationFormat()
        {
            DisplayName = "Reqnroll Undefined Step";
            ForegroundColor = (Color) ColorConverter.ConvertFromString("#887DBA");
        }
    }


    [Export] [Name(StepParameter)] [BaseDefinition("string")]
    private static ClassificationTypeDefinition _stepParameterClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = StepParameter)]
    [Name(StepParameter)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinStepParameterClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinStepParameterClassificationFormat()
        {
            DisplayName = "Reqnroll Step Parameter";
        }
    }


    [Export] [Name(ScenarioOutlinePlaceholder)] [BaseDefinition("number")]
    private static ClassificationTypeDefinition _scenarioOutlinePlaceholderClassificationTypeDefinition;

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = ScenarioOutlinePlaceholder)]
    [Name(ScenarioOutlinePlaceholder)]
    [UserVisible(true)]
    [Order(Before = Priority.Default)]
    internal sealed class GherkinScenarioOutlinePlaceholderClassificationFormat : ClassificationFormatDefinition
    {
        public GherkinScenarioOutlinePlaceholderClassificationFormat()
        {
            DisplayName = "Reqnroll Scenario Outline Placeholder";
            IsItalic = true;
        }
    }

#pragma warning restore 169
}
