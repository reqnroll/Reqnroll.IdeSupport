using System;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Payload for the <c>reqnroll/semanticTokens</c> server-to-client notification.
/// </summary>
/// <remarks>
/// The server pushes this to the Visual Studio client whenever a feature file's tokens change,
/// because VS's built-in LSP semantic-token colorizer cannot map Reqnroll's custom token types
/// and does not reliably pull tokens. The VS client decodes <see cref="Data"/> with the legend
/// from the <c>initialize</c> response and colours the file via its own classifier. Other IDE
/// clients ignore this notification and use the standard pull-based semantic tokens flow.
/// </remarks>
public sealed class PublishSemanticTokensParams
{
    /// <summary>The document URI these tokens apply to.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>The document version the tokens were encoded from.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The LSP semantic-token data (flat 5-int relative encoding). The complete token set when
    /// <see cref="StartLine"/> is null; otherwise only the tokens on <see cref="StartLine"/>..<see cref="EndLine"/>.
    /// </summary>
    public int[] Data { get; set; } = Array.Empty<int>();

    /// <summary>
    /// First line (inclusive) of a ranged push: the client replaces its tokens on
    /// <see cref="StartLine"/>..<see cref="EndLine"/> with <see cref="Data"/> and keeps the rest.
    /// Null when <see cref="Data"/> is the complete set, which replaces everything the client holds.
    /// </summary>
    public int? StartLine { get; set; }

    /// <summary>Last line (inclusive) of a ranged push; see <see cref="StartLine"/>.</summary>
    public int? EndLine { get; set; }

    /// <summary>
    /// Number of tokens in the complete set once this push is applied. Lets the client check that a ranged
    /// push left it holding what the server holds; null when the sender does not say.
    /// </summary>
    public int? TokenCount { get; set; }
}
