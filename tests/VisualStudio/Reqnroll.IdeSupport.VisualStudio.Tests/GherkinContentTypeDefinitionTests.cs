using System;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.Editor;
using Xunit;
using Reqnroll.IdeSupport.VisualStudio.Extension.Documents;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Pins the static MEF registration of the <c>Gherkin</c> content type (issue #78) to the
/// VisualStudio.Extensibility document type it must coexist with: VS reuses an existing content
/// type of the same name, so a different name or base would silently split them.
/// </summary>
public class GherkinContentTypeDefinitionTests
{
    private static FieldInfo Field(string name) =>
        typeof(GherkinContentTypeDefinition).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{name} not found");

    [Fact]
    public void Registers_the_Gherkin_content_type_on_the_language_server_base()
    {
        var field = Field(nameof(GherkinContentTypeDefinition.GherkinContentType));

        field.GetCustomAttribute<NameAttribute>()!.Name.Should().Be(VsWellKnownIds.GherkinContentType);
        field.GetCustomAttributes<BaseDefinitionAttribute>().Select(b => b.BaseDefinition)
            .Should().Equal(CodeRemoteContentDefinition.CodeRemoteContentTypeName);
    }

    [Fact]
    public void Maps_the_feature_extension_to_the_Gherkin_content_type()
    {
        var field = Field(nameof(GherkinContentTypeDefinition.FeatureFileExtensionMapping));

        field.GetCustomAttribute<FileExtensionAttribute>()!.FileExtension.Should().Be(".feature");
        field.GetCustomAttribute<ContentTypeAttribute>()!.ContentTypes.Should().Be(VsWellKnownIds.GherkinContentType);
    }

    [Fact]
    public void Uses_the_same_base_as_the_VisualStudio_Extensibility_document_type()
    {
        // LanguageServerProvider.LanguageServerBaseDocumentType is the base GherkinDocumentType
        // declares. Read by reflection: the Extensibility SDK is a private build-time reference of
        // the Extension project, so this test cannot compile against it, but it is in the output.
        var provider = Type.GetType(
            "Microsoft.VisualStudio.Extensibility.LanguageServer.LanguageServerProvider, Microsoft.VisualStudio.Extensibility",
            throwOnError: true)!;
        var baseDocumentType = provider.GetField("LanguageServerBaseDocumentType")!.GetRawConstantValue();

        baseDocumentType.Should().Be(CodeRemoteContentDefinition.CodeRemoteContentTypeName);
    }

    [Fact]
    public void Exports_are_MEF_exports()
    {
        Field(nameof(GherkinContentTypeDefinition.GherkinContentType))
            .GetCustomAttribute<System.ComponentModel.Composition.ExportAttribute>().Should().NotBeNull();
        Field(nameof(GherkinContentTypeDefinition.FeatureFileExtensionMapping))
            .GetCustomAttribute<System.ComponentModel.Composition.ExportAttribute>().Should().NotBeNull();
    }
}
