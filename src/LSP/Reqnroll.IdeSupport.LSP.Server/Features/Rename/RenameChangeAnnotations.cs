namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Annotation ids tagging the two kinds of edit a step rename produces, referenced by
/// <see cref="RenameHandler.HandleRenameAsync"/> via <see cref="WorkspaceEditBuilder"/>.
/// A compliant client (LSP 3.16 <c>changeAnnotationSupport</c>) groups the resulting
/// <c>WorkspaceEdit</c> preview by these ids instead of applying every edit silently.
/// </summary>
internal static class RenameChangeAnnotations
{
    /// <summary>Tags edits to <c>.feature</c> step-text usages.</summary>
    public const string Feature = "reqnroll.rename.feature";

    /// <summary>Tags the edit to the C# step-definition attribute literal.</summary>
    public const string Binding = "reqnroll.rename.binding";
}
