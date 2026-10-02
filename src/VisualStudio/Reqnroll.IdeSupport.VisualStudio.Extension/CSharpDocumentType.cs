namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// The built-in Visual Studio document type name for C# files. VS.Extensibility's
/// <c>DocumentType.KnownValues</c> only exposes "text", "code", and "plaintext" — there is no
/// SDK-provided constant for "CSharp" — so this mirrors <see cref="GherkinDocumentType"/> for our
/// own usages of it.
/// </summary>
internal static class CSharpDocumentType
{
    internal const string CSharp = "CSharp";
}
