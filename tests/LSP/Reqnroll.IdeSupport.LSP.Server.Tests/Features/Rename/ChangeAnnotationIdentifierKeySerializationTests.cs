using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

/// <summary>
/// Regression coverage for the wire-format bug fixed by
/// <see cref="ChangeAnnotationIdentifierKeySerialization"/>: Newtonsoft.Json — the actual LSP wire
/// serializer — resolves non-string <c>Dictionary</c> keys via <see cref="System.ComponentModel.TypeConverter"/>,
/// not via registered <c>JsonConverter</c>s. Without a <see cref="System.ComponentModel.TypeConverter"/>
/// for <see cref="ChangeAnnotationIdentifier"/>, a <c>WorkspaceEdit.ChangeAnnotations</c> key
/// serialized to <c>"changeAnnotationIdentifier { Identifier = ... }"</c> instead of the plain id,
/// so no client could ever match it against an edit's <c>annotationId</c> — silently dropping the
/// grouped/labelled preview and <c>needsConfirmation</c> prompt. <see cref="WorkspaceEditBuilderTests"/>
/// doesn't catch this because it asserts against the in-memory <see cref="ChangeAnnotationIdentifier"/>
/// object graph, never through the JSON serializer that actually crosses the wire.
/// </summary>
public class ChangeAnnotationIdentifierKeySerializationTests
{
    // Mirrors the CamelCasePropertyNamesContractResolver settings the server uses on the wire
    // (see LanguageServerOptionsExtensions.CamelCaseSerializer).
    private static readonly JsonSerializerSettings WireSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    [Fact]
    public void ChangeAnnotations_dictionary_keys_serialize_as_the_plain_annotation_id()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.Add(
            DocumentUri.FromFileSystemPath("/workspace/test.feature"),
            new LspRange(new Position(2, 4), new Position(2, 22)),
            "feature text",
            RenameChangeAnnotations.Feature);

        var edit = builder.Build();

        var json = JsonConvert.SerializeObject(edit, WireSettings);

        json.Should().Contain("\"reqnroll.rename.feature\":");
        json.Should().NotContain("changeAnnotationIdentifier");
    }

    [Fact]
    public void ChangeAnnotations_key_round_trips_to_match_the_edits_AnnotationId_on_the_wire()
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: true);
        builder.DeclareAnnotation(RenameChangeAnnotations.Feature, new ChangeAnnotation { Label = "Rename step usages" });
        builder.Add(
            DocumentUri.FromFileSystemPath("/workspace/test.feature"),
            new LspRange(new Position(2, 4), new Position(2, 22)),
            "feature text",
            RenameChangeAnnotations.Feature);

        var edit = builder.Build();

        var jObject = JObject.FromObject(edit, JsonSerializer.Create(WireSettings));
        var annotationIdOnEdit = jObject["documentChanges"]![0]!["edits"]![0]!["annotationId"]!.Value<string>();
        var changeAnnotationKeys = ((JObject)jObject["changeAnnotations"]!).Properties().Select(p => p.Name);

        changeAnnotationKeys.Should().Contain(annotationIdOnEdit);
    }
}
