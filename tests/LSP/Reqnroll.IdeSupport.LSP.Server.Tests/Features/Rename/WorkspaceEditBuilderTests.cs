using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class WorkspaceEditBuilderTests
{
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static readonly LspRange SomeRange = new(new Position(2, 4), new Position(2, 22));

    [Fact]
    public void IsEmpty_is_true_before_any_edit_is_added()
    {
        new WorkspaceEditBuilder(supportsChangeAnnotations: true).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void IsEmpty_is_false_once_an_edit_is_added()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.Add(FeatureUri, SomeRange, "new text");

        builder.IsEmpty.Should().BeFalse();
    }

    // ── Test condition 2 (plan §5.2): unsupported client — byte-identical to pre-#70 shape ──

    [Fact]
    public void Build_emits_the_legacy_Changes_shape_when_the_client_does_not_support_annotations()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.Add(FeatureUri, SomeRange, "new text", RenameChangeAnnotations.Feature);

        var edit = builder.Build();

        edit.Changes.Should().ContainKey(FeatureUri);
        var onlyEdit = edit.Changes![FeatureUri].Should().ContainSingle().Subject;
        onlyEdit.NewText.Should().Be("new text");
        onlyEdit.Should().NotBeOfType<AnnotatedTextEdit>();
        edit.DocumentChanges.Should().BeNull();
        edit.ChangeAnnotations.Should().BeNull();
    }

    // ── Test condition 1: supported client — annotated DocumentChanges + catalogue ──

    [Fact]
    public void Build_emits_annotated_DocumentChanges_when_the_client_supports_annotations()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.DeclareAnnotation(RenameChangeAnnotations.Binding, new ChangeAnnotation { Label = "Update step-definition attribute" });
        builder.Add(FeatureUri, SomeRange, "feature text", RenameChangeAnnotations.Feature);
        builder.Add(CsUri, SomeRange, "\"cs text\"", RenameChangeAnnotations.Binding);

        var edit = builder.Build();

        edit.Changes.Should().BeNull();
        edit.DocumentChanges.Should().NotBeNull();

        var docChanges = edit.DocumentChanges!.ToList();
        docChanges.Should().HaveCount(2);
        docChanges.Should().OnlyContain(c => c.IsTextDocumentEdit);

        var featureEdit = docChanges.Single(c => c.TextDocumentEdit!.TextDocument.Uri == FeatureUri).TextDocumentEdit!;
        featureEdit.TextDocument.Version.Should().BeNull();
        var featureAnnotated = featureEdit.Edits.Single().Should().BeOfType<AnnotatedTextEdit>().Subject;
        ((string)featureAnnotated.AnnotationId).Should().Be(RenameChangeAnnotations.Feature);

        var csEdit = docChanges.Single(c => c.TextDocumentEdit!.TextDocument.Uri == CsUri).TextDocumentEdit!;
        var csAnnotated = csEdit.Edits.Single().Should().BeOfType<AnnotatedTextEdit>().Subject;
        ((string)csAnnotated.AnnotationId).Should().Be(RenameChangeAnnotations.Binding);

        edit.ChangeAnnotations.Should().ContainKey(RenameChangeAnnotations.Feature);
        edit.ChangeAnnotations.Should().ContainKey(RenameChangeAnnotations.Binding);
    }

    // ── Test condition 3: only annotations actually referenced by an edit appear in the catalogue ──

    [Fact]
    public void Build_omits_declared_annotations_that_no_edit_references()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.DeclareAnnotation(RenameChangeAnnotations.Binding, new ChangeAnnotation { Label = "Update step-definition attribute" });
        builder.Add(FeatureUri, SomeRange, "feature text", RenameChangeAnnotations.Feature);
        // No .cs edit added — the "binding" annotation was declared but never used.

        var edit = builder.Build();

        edit.ChangeAnnotations.Should().ContainKey(RenameChangeAnnotations.Feature);
        edit.ChangeAnnotations.Should().NotContainKey(RenameChangeAnnotations.Binding);
    }

    // ── Test condition 6: no dangling AnnotationId — every id an edit references exists in the catalogue ──

    [Fact]
    public void Every_AnnotationId_referenced_by_an_edit_exists_as_a_ChangeAnnotations_key()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.Add(FeatureUri, SomeRange, "feature text", RenameChangeAnnotations.Feature);

        var edit = builder.Build();

        var referencedIds = edit.DocumentChanges!
            .Select(c => c.TextDocumentEdit!)
            .SelectMany(td => td.Edits)
            .Cast<AnnotatedTextEdit>()
            .Select(a => (string)a.AnnotationId);

        referencedIds.Should().OnlyContain(id => edit.ChangeAnnotations!.Keys.Any(k => (string)k == id));
    }

    [Fact]
    public void TouchedUris_reflects_the_distinct_set_of_uris_added()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.Add(FeatureUri, SomeRange, "a");
        builder.Add(FeatureUri, SomeRange, "b");
        builder.Add(CsUri, SomeRange, "c");

        builder.TouchedUris.Should().BeEquivalentTo(new[] { FeatureUri, CsUri });
    }

    [Fact]
    public void GetEditsByUri_groups_edits_by_document_regardless_of_negotiated_shape()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.Add(FeatureUri, SomeRange, "a");
        builder.Add(FeatureUri, SomeRange, "b");

        builder.GetEditsByUri()[FeatureUri].Should().HaveCount(2);
    }

    [Fact]
    public void An_edit_without_an_annotation_id_is_never_annotated_even_when_the_client_supports_annotations()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.Add(FeatureUri, SomeRange, "plain edit");

        builder.GetEditsByUri()[FeatureUri].Single().Should().NotBeOfType<AnnotatedTextEdit>();
    }

    // ── Issue #671 (R2): DocumentChanges carry the document version the edit was computed
    //    against, which is what OptionalVersionedTextDocumentIdentifier exists for — "to allow
    //    clients to check the text document version before an edit is applied." ────────────────

    private static OptionalVersionedTextDocumentIdentifier DocumentIdentifierFor(
        WorkspaceEdit edit, DocumentUri uri)
        => edit.DocumentChanges!
            .Select(change => change.TextDocumentEdit!)
            .Single(textDocumentEdit => textDocumentEdit.TextDocument.Uri == uri)
            .TextDocument;

    [Fact]
    public void Build_stamps_each_DocumentChange_with_the_resolved_document_version()
    {
        var builder = new WorkspaceEditBuilder(
            supportsChangeAnnotations: true,
            resolveVersion: uri => uri == FeatureUri ? 7 : 42);
        builder.Add(FeatureUri, SomeRange, "new text");
        builder.Add(CsUri, SomeRange, "new literal");

        var edit = builder.Build();

        DocumentIdentifierFor(edit, FeatureUri).Version.Should().Be(7);
        DocumentIdentifierFor(edit, CsUri).Version.Should().Be(42);
    }

    [Fact]
    public void Build_stamps_a_null_version_for_a_document_the_client_has_not_opened()
    {
        // Per the spec, null here means "the content on disk is the master" — the correct and
        // common case for a rename, which routinely edits closed .feature files.
        var builder = new WorkspaceEditBuilder(
            supportsChangeAnnotations: true,
            resolveVersion: _ => null);
        builder.Add(FeatureUri, SomeRange, "new text");

        DocumentIdentifierFor(builder.Build(), FeatureUri).Version.Should().BeNull();
    }

    [Fact]
    public void Build_stamps_a_null_version_when_no_resolver_is_supplied()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.Add(FeatureUri, SomeRange, "new text");

        DocumentIdentifierFor(builder.Build(), FeatureUri).Version.Should().BeNull();
    }

    [Fact]
    public void The_legacy_Changes_shape_is_unaffected_by_the_version_resolver()
    {
        // The legacy map has nowhere to carry a version, so a resolver must neither appear in the
        // output nor change its shape.
        var builder = new WorkspaceEditBuilder(
            supportsChangeAnnotations: false,
            resolveVersion: _ => 7);
        builder.Add(FeatureUri, SomeRange, "new text");

        var edit = builder.Build();

        edit.Changes.Should().ContainKey(FeatureUri);
        edit.DocumentChanges.Should().BeNull();
    }
}
