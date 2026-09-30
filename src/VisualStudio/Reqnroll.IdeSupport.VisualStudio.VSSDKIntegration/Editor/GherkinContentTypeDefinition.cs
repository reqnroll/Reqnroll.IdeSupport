#nullable disable
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Editor;

/// <summary>
/// Static MEF registration of the <c>Gherkin</c> content type and its <c>.feature</c> file
/// extension, so both exist the moment Visual Studio starts restoring documents (issue #78).
/// </summary>
/// <remarks>
/// <para>
/// The content type is also contributed through VisualStudio.Extensibility (<c>GherkinDocumentType</c>
/// in the Extension project), and that registration alone is not enough. VS's
/// <c>ExtensionContentTypeSectionTracker</c> (<c>Microsoft.VisualStudio.Editor.Implementation.dll</c>)
/// adds VS.Extensibility document types to the content-type and file-extension registries
/// asynchronously, from a fire-and-forget initialization. A <c>.feature</c> tab restored before that
/// finishes gets whatever content type <c>.feature</c> had at that moment. Nothing keyed on
/// <c>[ContentType("Gherkin")]</c> ever attaches to its view: no classifier, no CodeLens, no
/// navigation bar, no LSP view features. Only closing and reopening the tab recovered it. This was
/// confirmed live from a debug log in which the Gherkin-scoped navigation-bar listener fired for a
/// freshly opened file but never for the restored one.
/// </para>
/// <para>
/// MEF definitions are in the component cache, so they are available at restore time. The two
/// registrations coexist: the tracker's <c>RegisterExtension</c> reuses a content type that already
/// exists (<c>GetContentType</c> before <c>AddContentType</c>), and it leaves a file-extension
/// mapping alone when the mapped type already <c>IsOfType</c> the document type. Both must use the
/// same name and the same base, which is
/// <see cref="CodeRemoteContentDefinition.CodeRemoteContentTypeName"/> — the value of
/// <c>LanguageServerProvider.LanguageServerBaseDocumentType</c>, so the language server provider's
/// document filter still applies.
/// </para>
/// <para>
/// Buffers created before even this definition was available (a stale MEF cache, or another
/// extension that claimed <c>.feature</c>) are re-typed by <c>FeatureBufferContentTypeGuard</c>.
/// </para>
/// </remarks>
public static class GherkinContentTypeDefinition
{
    /// <summary>The file extension of Gherkin feature files.</summary>
    public const string FeatureFileExtension = ".feature";

    [Export]
    [Name(VsWellKnownIds.GherkinContentType)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition GherkinContentType;

    [Export]
    [FileExtension(FeatureFileExtension)]
    [ContentType(VsWellKnownIds.GherkinContentType)]
    internal static FileExtensionToContentTypeDefinition FeatureFileExtensionMapping;
}
