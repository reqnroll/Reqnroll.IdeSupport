using System.IO;
#nullable disable

using Gherkin.Ast;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// Parses feature file content into a <see cref="IdeSupportGherkinDocument"/> AST, adding
/// Reqnroll-specific semantic checks (duplicate scenario/example names, missing examples) on
/// top of the underlying Gherkin grammar parser.
/// </summary>
public class IdeSupportGherkinParser
{
    private readonly IErrorTelemetryService _telemetryService;
    private IAstBuilder<IdeSupportGherkinDocument> _astBuilder;

    /// <summary>Creates a parser using the given Gherkin dialect provider.</summary>
    public IdeSupportGherkinParser(IGherkinDialectProvider dialectProvider, IErrorTelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
        DialectProvider = dialectProvider;
    }

    /// <summary>Supplies the Gherkin dialect (language/keywords) used while tokenizing.</summary>
    public IGherkinDialectProvider DialectProvider { get; }
    internal IdeSupportGherkinAstBuilder AstBuilder => _astBuilder as IdeSupportGherkinAstBuilder;

    /// <summary>
    /// Parses <paramref name="featureFileContent"/>, tolerating parser errors: on failure it still
    /// returns a best-effort <paramref name="gherkinDocument"/> (with open AST nodes closed) and
    /// reports the individual errors via <paramref name="parserErrors"/>.
    /// </summary>
    /// <returns>True when parsing succeeded without errors.</returns>
    public bool ParseAndCollectErrors(string featureFileContent, IIdeSupportLogger logger,
        out IdeSupportGherkinDocument gherkinDocument, out List<ParserException> parserErrors)
    {
        var reader = new StringReader(featureFileContent);
        gherkinDocument = null;
        parserErrors = new List<ParserException>();
        try
        {
            gherkinDocument = Parse(reader, "foo.feature"); //TODO: remove unused path
            return true;
        }
        catch (ParserException parserException)
        {
            logger.LogVerbose($"ParserErrors: {parserException.Message}");
            gherkinDocument = GetResultOfInvalid();
            if (parserException is CompositeParserException compositeParserException)
                parserErrors.AddRange(compositeParserException.Errors);
            else
                parserErrors.Add(parserException);
        }
        catch (Exception e)
        {
            logger.LogException(_telemetryService, e, "Exception during Gherkin parsing");
            gherkinDocument = GetResult();
        }

        return false;
    }

    private IdeSupportGherkinDocument GetResultOfInvalid()
    {
        // trying to "finish" open nodes by sending dummy <endrule> messages up to 5 levels of nesting
        for (int i = 0; i < 10; i++)
        {
            var result = GetResult();
            if (result != null)
                return result;

            try
            {
                AstBuilder.EndRule(RuleType.None);
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Parses <paramref name="featureFileReader"/>'s content and runs the semantic checks,
    /// throwing a <see cref="ParserException"/> (or <see cref="CompositeParserException"/> if
    /// several were found) if any error occurs.
    /// </summary>
    public IdeSupportGherkinDocument Parse(TextReader featureFileReader, string sourceFilePath)
    {
        var tokenScanner = (ITokenScanner) new TokenScanner(featureFileReader);
        var tokenMatcher = new TokenMatcher(DialectProvider);
        _astBuilder = new IdeSupportGherkinAstBuilder(sourceFilePath, () => tokenMatcher.CurrentDialect);

        var parser = new InternalParser(_astBuilder, AstBuilder.RecordStateForLine, _telemetryService);
        var gherkinDocument = parser.Parse(tokenScanner, tokenMatcher);

        CheckSemanticErrors(gherkinDocument);

        return gherkinDocument;
    }

    /// <summary>Returns the AST built so far by the current <see cref="_astBuilder"/>.</summary>
    public IdeSupportGherkinDocument GetResult() => _astBuilder.GetResult();

    private class InternalParser : Parser<IdeSupportGherkinDocument>
    {
        private readonly IErrorTelemetryService _telemetryService;
        private readonly Action<int, int> _recordStateForLine;

        public InternalParser(IAstBuilder<IdeSupportGherkinDocument> astBuilder, Action<int, int> recordStateForLine,
            IErrorTelemetryService telemetryService)
            : base(astBuilder)
        {
            _recordStateForLine = recordStateForLine;
            _telemetryService = telemetryService;
        }

        public int NullMatchToken(int state, Token token) =>
            MatchToken(state, token, new ParserContext
            {
                Errors = new List<ParserException>(),
                TokenMatcher = new AllFalseTokenMatcher(),
                TokenQueue = new Queue<Token>(),
                TokenScanner = new NullTokenScanner()
            });

        protected override int MatchToken(int state, Token token, ParserContext context)
        {
            _recordStateForLine?.Invoke(token.Location.Line, state);
            try
            {
                return base.MatchToken(state, token, context);
            }
            catch (InvalidOperationException ex)
            {
                _telemetryService.MonitorError(ex);
                throw;
            }
        }
    }

    #region Semantic Errors

    /// <summary>Validates the parsed document for semantic (non-syntax) errors, such as inconsistent step keywords, and records them on the document.</summary>
    protected virtual void CheckSemanticErrors(IdeSupportGherkinDocument reqnrollDocument)
    {
        var errors = new List<ParserException>();

        errors.AddRange(((IdeSupportGherkinAstBuilder) _astBuilder).Errors);

        if (reqnrollDocument?.Feature != null)
        {
            CheckForDuplicateScenarios(reqnrollDocument.Feature, errors);

            CheckForDuplicateExamples(reqnrollDocument.Feature, errors);

            CheckForMissingExamples(reqnrollDocument.Feature, errors);

            CheckForRulesPreSpecFlow31(reqnrollDocument.Feature, errors);
        }

        // collect
        if (errors.Count == 1)
            throw errors[0];
        if (errors.Count > 1)
            throw new CompositeParserException(errors.ToArray());
    }

    private void CheckForRulesPreSpecFlow31(Feature feature, List<ParserException> errors)
    {
        //TODO: Show error when Rule keyword is used in SpecFlow v3.0 or earlier
    }

    private void CheckForDuplicateScenarios(Feature feature, List<ParserException> errors)
    {
        // duplicate scenario name
        var duplicatedScenarios = feature.FlattenScenarioDefinitions().GroupBy(sd => sd.Name, sd => sd)
            .Where(g => g.Count() > 1).ToArray();
        errors.AddRange(
            duplicatedScenarios.Select(g =>
                new SemanticParserException(
                    $"Feature file already contains a scenario with name '{g.Key}'",
                    g.ElementAt(1).Location)));
    }

    private void CheckForDuplicateExamples(Feature feature, List<ParserException> errors)
    {
        foreach (var scenarioOutline in feature.FlattenScenarioDefinitions().OfType<ScenarioOutline>())
        {
            var duplicateExamples = scenarioOutline.Examples
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Where(e => e.Tags.All(t => t.Name != "ignore"))
                .GroupBy(e => e.Name, e => e).Where(g => g.Count() > 1);

            foreach (var duplicateExample in duplicateExamples)
            {
                var message =
                    $"Scenario Outline '{scenarioOutline.Name}' already contains an example with name '{duplicateExample.Key}'";
                var semanticParserException =
                    new SemanticParserException(message, duplicateExample.ElementAt(1).Location);
                errors.Add(semanticParserException);
            }
        }
    }

    private void CheckForMissingExamples(Feature feature, List<ParserException> errors)
    {
        foreach (var scenarioOutline in feature.FlattenScenarioDefinitions().OfType<ScenarioOutline>())
            if (DoesntHavePopulatedExamples(scenarioOutline))
            {
                var message = $"Scenario Outline '{scenarioOutline.Name}' has no examples defined";
                var semanticParserException = new SemanticParserException(message, scenarioOutline.Location);
                errors.Add(semanticParserException);
            }
    }

    private static bool DoesntHavePopulatedExamples(ScenarioOutline scenarioOutline)
    {
        return !scenarioOutline.Examples.Any() ||
               scenarioOutline.Examples.Any(x => x.TableBody == null || !x.TableBody.Any());
    }

    #endregion

    #region Expected tokens

    private class NullAstBuilder : IAstBuilder<IdeSupportGherkinDocument>
    {
        public void Build(Token token)
        {
        }

        public void StartRule(RuleType ruleType)
        {
        }

        public void EndRule(RuleType ruleType)
        {
        }

        public IdeSupportGherkinDocument GetResult() => null;

        public void Reset()
        {
        }
    }

    private class AllFalseTokenMatcher : ITokenMatcher
    {
        public bool Match_EOF(Token token) => false;

        public bool Match_Empty(Token token) => false;

        public bool Match_Comment(Token token) => false;

        public bool Match_TagLine(Token token) => false;

        public bool Match_FeatureLine(Token token) => false;

        public bool Match_RuleLine(Token token) => false;

        public bool Match_BackgroundLine(Token token) => false;

        public bool Match_ScenarioLine(Token token) => false;

        public bool Match_ExamplesLine(Token token) => false;

        public bool Match_StepLine(Token token) => false;

        public bool Match_DocStringSeparator(Token token) => false;

        public bool Match_TableRow(Token token) => false;

        public bool Match_Language(Token token) => false;

        public bool Match_Other(Token token) => false;

        public void Reset()
        {
        }
    }

    private class NullTokenScanner : ITokenScanner
    {
        public Token Read() => new Token(new Location());
    }

    /// <summary>
    /// Runs the parser's state machine from <paramref name="state"/> with a dummy end-of-file
    /// token to discover which token types would have been valid next; used to power
    /// error-recovery/completion suggestions.
    /// </summary>
    public static TokenType[] GetExpectedTokens(int state, IErrorTelemetryService telemetryService)
    {
        var parser = new InternalParser(new NullAstBuilder(), null, telemetryService)
        {
            StopAtFirstError = true
        };

        try
        {
            parser.NullMatchToken(state, new Token(new Location()));
        }
        catch (UnexpectedEOFException ex)
        {
            return ex.ExpectedTokenTypes.Select(type => (TokenType) Enum.Parse(typeof(TokenType), type.TrimStart('#')))
                .ToArray();
        }

        return new TokenType[0];
    }

    #endregion
}
