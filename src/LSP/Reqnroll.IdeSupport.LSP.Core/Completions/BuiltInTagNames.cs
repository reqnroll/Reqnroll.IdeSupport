namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// Gherkin tags the tag completion offers regardless of whether any file in the project has
/// used them yet — the completion list is never empty even on a fresh project (issue #828).
/// </summary>
public static class BuiltInTagNames
{
    /// <summary>The built-in <c>@ignore</c> tag: Reqnroll skips the tagged feature/scenario in the test run.</summary>
    public const string Ignore = "@ignore";
}
