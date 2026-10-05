using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Newtonsoft.Json — the LSP wire serializer — resolves non-string <c>Dictionary</c> keys via
/// <see cref="TypeConverter"/>, not via registered <c>JsonConverter</c>s (those only apply to
/// values). <see cref="WorkspaceEdit.ChangeAnnotations"/> is keyed by
/// <see cref="ChangeAnnotationIdentifier"/>, which has no <see cref="TypeConverter"/> of its own,
/// so without this registration Newtonsoft falls back to the type's default <c>ToString()</c> —
/// e.g. <c>"changeAnnotationIdentifier { Identifier = reqnroll.rename.feature }"</c> — instead of
/// the plain id. That mismatches the <c>annotationId</c> each <see cref="AnnotatedTextEdit"/>
/// references, so compliant clients (VS Code) can never resolve the annotation and silently apply
/// the edit without the grouped/labelled preview or <c>needsConfirmation</c> prompt
/// (<see cref="RenameChangeAnnotations"/>, <see cref="WorkspaceEditBuilder"/>).
/// </summary>
internal static class ChangeAnnotationIdentifierKeySerialization
{
    [ModuleInitializer]
    public static void Register()
        => TypeDescriptor.AddAttributes(
            typeof(ChangeAnnotationIdentifier),
            new TypeConverterAttribute(typeof(ChangeAnnotationIdentifierTypeConverter)));

    private sealed class ChangeAnnotationIdentifierTypeConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
            => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
            => destinationType == typeof(string) && value is ChangeAnnotationIdentifier id
                ? (string)id
                : base.ConvertTo(context, culture, value, destinationType);
    }
}
