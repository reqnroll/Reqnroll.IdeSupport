#nullable disable
using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>Convenience extensions for walking and querying an <see cref="IGherkinDocumentContext"/> chain.</summary>
public static class GherkinDocumentContextExtensions
{
    /// <summary>Yields this context's node and every ancestor's node, innermost first.</summary>
    public static IEnumerable<object> GetNodes(this IGherkinDocumentContext context)
    {
        while (context != null)
        {
            if (context.Node != null)
                yield return context.Node;
            context = context.Parent;
        }
    }

    /// <summary>Yields the ancestor-chain nodes that are of type <typeparamref name="T"/>.</summary>
    public static IEnumerable<T> GetNodes<T>(this IGherkinDocumentContext context)
        => context.GetNodes().OfType<T>();

    /// <summary>Returns all tags declared on any tag-bearing node in the ancestor chain.</summary>
    public static IEnumerable<Tag> GetTags(this IGherkinDocumentContext context)
    {
        return context.GetNodes<IHasTags>().SelectMany(ht => ht.Tags);
    }

    /// <summary>Returns the names of all tags declared on any tag-bearing node in the ancestor chain.</summary>
    public static IEnumerable<string> GetTagNames(this IGherkinDocumentContext context)
        => context.GetTags().Select(t => t.Name);

    /// <summary>True when this context's node is a <see cref="ScenarioOutline"/>.</summary>
    public static bool IsScenarioOutline(this IGherkinDocumentContext context)
        => context?.Node is ScenarioOutline;

    /// <summary>True when this context's node is a <see cref="Background"/>.</summary>
    public static bool IsBackground(this IGherkinDocumentContext context)
        => context?.Node is Background;

    /// <summary>Walks up the ancestor chain and returns the nearest context whose node is of type <typeparamref name="T"/>.</summary>
    public static IGherkinDocumentContext GetParentOf<T>(this IGherkinDocumentContext context)
    {
        while (context.Parent is {Node: not T}) context = context.Parent;
        return context.Parent;
    }

    /// <summary>Returns this context's node if it is a <typeparamref name="T"/>, otherwise the nearest ancestor node that is.</summary>
    public static T AncestorOrSelfNode<T>(this IGherkinDocumentContext context)
        where T : class
    {
        if (context.Node is T node)
            return node;

        var parentOf = GetParentOf<T>(context);
        return parentOf?.Node as T;
    }
}
