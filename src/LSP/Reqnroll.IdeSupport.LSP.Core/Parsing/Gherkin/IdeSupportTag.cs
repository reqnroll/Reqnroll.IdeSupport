#nullable disable

using Reqnroll.IdeSupport.LSP.Core.Documents;





namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// A single flattened node produced while walking a parsed feature document (e.g. a feature
/// block, step, tag, or step match), identified by a <see cref="IdeSupportTagTypes"/> constant,
/// its text span, and an optional payload. Tags form a tree via <see cref="ParentTag"/>/
/// <see cref="ChildTags"/> and back the semantic tokens, diagnostics, and binding-match features.
/// </summary>
public record IdeSupportTag(string Type, GherkinRange Range, object Data = null) : IGherkinDocumentContext
{
    private readonly List<IdeSupportTag> _childTags = new();

    /// <summary>The enclosing tag, or a <see cref="VoidIdeSupportTag"/> at the root.</summary>
    public IdeSupportTag ParentTag { get; protected internal set; }
    /// <summary>The tags nested directly within this one.</summary>
    public ICollection<IdeSupportTag> ChildTags => _childTags;
    /// <summary>True when <see cref="Type"/> denotes an error tag (e.g. ParserError, BindingError).</summary>
    public bool IsError => Type.EndsWith("Error");

    IGherkinDocumentContext IGherkinDocumentContext.Parent => ParentTag;
    object IGherkinDocumentContext.Node => Data;

    internal virtual IdeSupportTag AddChild(IdeSupportTag childTag)
    {
        childTag.ParentTag = this;
        _childTags.Add(childTag);
        return childTag;
    }

    /// <summary>Returns a short "Type:Range" description for diagnostics/logging.</summary>
    public override string ToString() => $"{Type}:{Range}";

    /// <summary>Recursively returns every descendant tag whose <see cref="Type"/> matches <paramref name="type"/>.</summary>
    public IEnumerable<IdeSupportTag> GetDescendantsOfType(string type)
    {
        foreach (var childTag in ChildTags)
        {
            if (childTag.Type == type)
                yield return childTag;

            foreach (var descendantTag in childTag.GetDescendantsOfType(type)) yield return descendantTag;
        }
    }
}
