using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;





namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// Walks a parsed feature document and produces the flattened <c>IdeSupportTag</c> tree consumed
/// for semantic tokens, diagnostics, and step binding matches.
/// </summary>
public interface IIdeSupportTagParser
{
    /// <summary>
    /// Parse <paramref name="fileSnapshot"/> and return Deveroom tags annotated with
    /// binding matches from <paramref name="bindingRegistry"/>.
    /// Pass <see cref="ProjectBindingRegistry.Invalid"/> when no registry is available yet;
    /// step-matching tags will simply be omitted.
    /// </summary>
    IReadOnlyCollection<IdeSupportTag> Parse(
        IGherkinTextSnapshot fileSnapshot,
        ProjectBindingRegistry bindingRegistry);

    /// <summary>
    /// Parse <paramref name="fileSnapshot"/> for a feature file owned by several projects.
    /// <paramref name="bindingRegistries"/> holds one registry per owning project, <b>primary
    /// owner first</b> (must be non-empty). A step is reported undefined only when no owner's
    /// registry binds it; otherwise it carries the first binding match found (primary owner
    /// preferred). If the primary registry is <see cref="ProjectBindingRegistry.Invalid"/>, step
    /// matching is skipped exactly as in the single-registry overload. Hook matching uses the
    /// primary owner only.
    /// </summary>
    IReadOnlyCollection<IdeSupportTag> Parse(
        IGherkinTextSnapshot fileSnapshot,
        IReadOnlyList<ProjectBindingRegistry> bindingRegistries);
}
