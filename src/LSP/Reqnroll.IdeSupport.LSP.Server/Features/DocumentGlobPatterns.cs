namespace Reqnroll.IdeSupport.LSP.Server.Features;

/// <summary>
/// Glob patterns used across <see cref="OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentFilter"/>
/// registrations to scope handlers to the document types the server cares about.
/// </summary>
internal static class DocumentGlobPatterns
{
    internal const string FeatureFilePattern = "**/*.feature";
    internal const string CSharpFilePattern = "**/*.cs";
}
