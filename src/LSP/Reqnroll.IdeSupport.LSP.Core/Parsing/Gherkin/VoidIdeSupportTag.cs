using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// A null-object <see cref="IdeSupportTag"/> used as a root/placeholder parent when no real tag
/// is available, so callers can walk <c>ParentTag</c> chains without null-checking.
/// </summary>
public record VoidIdeSupportTag : IdeSupportTag
{
    /// <summary>The single shared instance of this null-object tag.</summary>
    public static VoidIdeSupportTag Instance = new();

    private VoidIdeSupportTag() : base("Void", GherkinRange.Empty, new object())
    {
    }

    internal override IdeSupportTag AddChild(IdeSupportTag childTag)
    {
        childTag.ParentTag = this;
        return childTag;
    }
}
