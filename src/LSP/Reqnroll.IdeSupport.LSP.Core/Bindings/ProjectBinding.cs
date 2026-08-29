#nullable disable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Base class for a discovered Reqnroll binding (step definition or hook): the scope it's
/// restricted to, the method it maps to, and any error recorded while importing it.
/// </summary>
public class ProjectBinding
{
    /// <summary>The tag/feature/scenario scope restricting where this binding applies, or null for no restriction.</summary>
    public BindingScope Scope { get; }
    /// <summary>The bound method's identity (name, parameter types, source location).</summary>
    public ProjectBindingImplementation Implementation { get; }
    /// <summary>True when the binding has no import error and its scope (if any) is valid.</summary>
    public virtual bool IsValid => Error == null && Scope?.IsValid != false;
    /// <summary>A description of why this binding could not be imported/used, or null if it's valid.</summary>
    public string Error { get; }
    /// <summary>
    /// Where a diagnostic for <see cref="Error"/> should be anchored, when it's more specific than
    /// the method itself — e.g. the <c>[Given(...)]</c> attribute for a malformed step expression,
    /// or the binding's own attribute for an invalid scope tag expression (issue #514 follow-up).
    /// <see langword="null"/> for a structural (method/type-level) error, which is shared by every
    /// attribute on the method and should fall back to <see cref="ProjectBindingImplementation.SourceLocation"/>
    /// so it dedupes to one squiggle rather than one per attribute. Only populated by
    /// <c>StepDefinitionFileParser</c> (Roslyn); the connector's PDB-derived data has no
    /// per-attribute location to offer.
    /// </summary>
    public SourceLocation ErrorLocation { get; }

    /// <summary>Creates a binding from its implementation, scope, and optional error.</summary>
    public ProjectBinding(ProjectBindingImplementation implementation, BindingScope scope, string error = null,
        SourceLocation errorLocation = null)
    {
        Implementation = implementation;
        Scope = scope;
        Error = error;
        ErrorLocation = errorLocation;
    }

    /// <summary>Checks whether this binding's <see cref="Scope"/> (tag expression, feature title, scenario title) matches the given context.</summary>
    protected bool MatchScope(IGherkinDocumentContext context)
    {
        if (Scope != null)
        {
            if (Scope.Tag != null && !Scope.Tag.Evaluate(context.GetTagNames()))
                return false;
            if (Scope.FeatureTitle != null && context.AncestorOrSelfNode<Feature>()?.Name != Scope.FeatureTitle)
                return false;
            if (Scope.ScenarioTitle != null && context.AncestorOrSelfNode<Scenario>()?.Name != Scope.ScenarioTitle)
                return false;
        }

        return true;
    }
}