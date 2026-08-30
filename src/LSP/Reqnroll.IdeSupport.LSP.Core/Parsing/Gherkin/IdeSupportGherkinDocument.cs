using Gherkin.Ast;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// A parsed Gherkin document extended with the dialect it was parsed under and per-line parser
/// state, letting callers ask what tokens would be valid at a given line (used for error
/// recovery / completion suggestions).
/// </summary>
public class IdeSupportGherkinDocument : GherkinDocument
{
    private readonly List<int> _statesForLines;

    /// <summary>Creates a document from its parsed parts plus per-line parser state.</summary>
    public IdeSupportGherkinDocument(Feature feature, IEnumerable<Comment> comments, string sourceFilePath,
        GherkinDialect gherkinDialect, List<int> statesForLines) : base(feature, comments)
    {
        _statesForLines = statesForLines;
        GherkinDialect = gherkinDialect;
    }

    /// <summary>The Gherkin dialect (language/keywords) this document was parsed with.</summary>
    public GherkinDialect GherkinDialect { get; }

    /// <summary>Returns the token types that would have been valid at the given line, or empty if unknown.</summary>
    public TokenType[] GetExpectedTokens(int line, IErrorTelemetryService telemetryService)
    {
        if (_statesForLines.Count <= line)
            return new TokenType[0];

        var state = _statesForLines[line];
        if (state < 0)
            return new TokenType[0];
        return IdeSupportGherkinParser.GetExpectedTokens(state, telemetryService);
    }
}
