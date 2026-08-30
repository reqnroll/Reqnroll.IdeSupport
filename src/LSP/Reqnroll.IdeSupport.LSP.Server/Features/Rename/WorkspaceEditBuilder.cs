using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Accumulates the per-file text edits of a rename and emits a <see cref="WorkspaceEdit"/> in
/// whichever shape the requesting client actually supports:
/// <list type="bullet">
/// <item><description><b>Annotated</b> — <see cref="WorkspaceEdit.DocumentChanges"/> of
/// <see cref="AnnotatedTextEdit"/>, plus a <see cref="WorkspaceEdit.ChangeAnnotations"/>
/// catalogue — when the client negotiated LSP 3.16 change-annotation support (see
/// docs/Rename-ChangeAnnotations-Implementation-Plan.md). Compliant clients (e.g. VS Code)
/// render this as a grouped, labelled rename preview.</description></item>
/// <item><description><b>Plain</b> — the legacy <see cref="WorkspaceEdit.Changes"/> map — for
/// every other client (Visual Studio does not advertise <c>changeAnnotationSupport</c> as of
/// Phase 0's capability survey). Byte-identical to the handler's pre-#70 output.</description></item>
/// </list>
/// Centralising the branch here keeps <see cref="RenameHandler.HandleRenameAsync"/> free of
/// shape-negotiation logic — it only calls <see cref="Add"/> and <see cref="Build"/>.
/// </summary>
internal sealed class WorkspaceEditBuilder
{
    private readonly bool _supportsChangeAnnotations;
    private readonly Dictionary<ChangeAnnotationIdentifier, ChangeAnnotation> _annotationCatalogue = new();
    private readonly HashSet<ChangeAnnotationIdentifier> _usedAnnotationIds = new();
    private readonly List<(DocumentUri Uri, TextEdit Edit)> _edits = new();

    public WorkspaceEditBuilder(bool supportsChangeAnnotations)
        => _supportsChangeAnnotations = supportsChangeAnnotations;

    /// <summary>Whether this builder is emitting the annotated <c>DocumentChanges</c> shape.</summary>
    public bool SupportsChangeAnnotations => _supportsChangeAnnotations;

    /// <summary>True when no edits have been added yet — callers should return <see langword="null"/> rather than <see cref="Build"/> an empty edit.</summary>
    public bool IsEmpty => _edits.Count == 0;

    /// <summary>The distinct set of document URIs touched so far, in first-added order.</summary>
    public IReadOnlyCollection<DocumentUri> TouchedUris
        => _edits.Select(e => e.Uri).Distinct().ToList();

    /// <summary>
    /// Registers a <see cref="ChangeAnnotation"/> under <paramref name="annotationId"/>. Only
    /// annotations actually referenced by an added edit are included in <see cref="Build"/>'s
    /// output (an edit whose annotation id has no catalogue entry would be a protocol-validity
    /// violation, and the reverse — a declared-but-unused annotation — is simply noise for the
    /// client to render).
    /// </summary>
    public void DeclareAnnotation(string annotationId, ChangeAnnotation annotation)
        => _annotationCatalogue[annotationId] = annotation;

    /// <summary>
    /// Adds one edit for <paramref name="uri"/>. When <paramref name="annotationId"/> is
    /// non-null and the client supports change annotations, the edit is emitted as an
    /// <see cref="AnnotatedTextEdit"/> tagged with that id; otherwise (including whenever the
    /// client doesn't support annotations at all) it is a plain <see cref="TextEdit"/>.
    /// </summary>
    public void Add(DocumentUri uri, LspRange range, string newText, string? annotationId = null)
    {
        TextEdit edit = _supportsChangeAnnotations && annotationId is not null
            ? new AnnotatedTextEdit { Range = range, NewText = newText, AnnotationId = annotationId }
            : new TextEdit { Range = range, NewText = newText };

        if (_supportsChangeAnnotations && annotationId is not null)
            _usedAnnotationIds.Add(annotationId);

        _edits.Add((uri, edit));
    }

    /// <summary>
    /// Groups the accumulated edits by document URI. Exposed so callers that need the plain
    /// per-file edit list outside the returned <see cref="WorkspaceEdit"/> — e.g. the VS-only
    /// <c>workspace/applyEdit</c> push in <see cref="RenameHandler.HandleRenameAsync"/>,
    /// which VS's rename-interception pipe requires regardless of this builder's negotiated
    /// shape — don't have to re-derive it.
    /// </summary>
    public IReadOnlyDictionary<DocumentUri, List<TextEdit>> GetEditsByUri()
        => _edits.GroupBy(e => e.Uri).ToDictionary(g => g.Key, g => g.Select(e => e.Edit).ToList());

    /// <summary>Emits the negotiated <see cref="WorkspaceEdit"/> shape from the accumulated edits.</summary>
    public WorkspaceEdit Build()
    {
        var editsByUri = GetEditsByUri();

        if (!_supportsChangeAnnotations)
        {
            return new WorkspaceEdit
            {
                Changes = editsByUri.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<TextEdit>)kvp.Value)
            };
        }

        return new WorkspaceEdit
        {
            DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                editsByUri.Select(kvp => new WorkspaceEditDocumentChange(new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = kvp.Key, Version = null },
                    Edits = new TextEditContainer(kvp.Value)
                }))),
            ChangeAnnotations = _annotationCatalogue
                .Where(kvp => _usedAnnotationIds.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }
}
