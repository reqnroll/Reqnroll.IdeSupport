#nullable disable

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

/// <summary>
/// Decides whether a step-definition expression string should be parsed as a Cucumber
/// Expression (via <see cref="global::CucumberExpressions.CucumberExpression"/>) or as a plain,
/// already-valid regex.
/// </summary>
/// <remarks>
/// Faithfully ported (not merely re-derived) from Reqnroll's own runtime
/// <c>Reqnroll.Bindings.CucumberExpressions.CucumberExpressionDetector</c> (as of Reqnroll
/// 3.2.0/17.1.0-era sources) rather than re-implemented from scratch, since that class lives in
/// the <c>Reqnroll</c> runtime assembly itself — which <c>LSP.Core</c> deliberately does not
/// reference, so its build/discovery can run purely from syntax, without needing the target
/// project's Reqnroll version to have been restored or built. The <c>Cucumber.CucumberExpressions</c>
/// package that supplies the actual expression grammar has no equivalent detector of its own; that
/// decision is left entirely up to each consumer (Reqnroll's runtime included), so there is
/// nothing to take as a dependency for this half of the problem — only to port. Keep this in sync
/// by inspection if Reqnroll's own detector logic changes.
/// </remarks>
internal static class CucumberExpressionDetector
{
    private static readonly Regex ParameterPlaceholder = new(@"{\w*}");
    private static readonly Regex CommonRegexStepDefPatterns = new(@"(\([^\)]+[\*\+]\)|\.\*)");
    private static readonly Regex ExtendedRegexStepDefPatterns = new(@"(\\\.|\\d\+)"); // \. \d+

    public static bool IsCucumberExpression(string cucumberExpressionCandidate)
    {
        if (cucumberExpressionCandidate.StartsWith("^") || cucumberExpressionCandidate.EndsWith("$"))
            return false;

        if (ParameterPlaceholder.IsMatch(cucumberExpressionCandidate))
            return true;

        if (CommonRegexStepDefPatterns.IsMatch(cucumberExpressionCandidate))
            return false;

        // These are special constructs that usually happen in regex, but not valid
        // in Cucumber Expressions => If they exist, we treat the expression as regex.
        // - \d+
        // - \.
        if (ExtendedRegexStepDefPatterns.IsMatch(cucumberExpressionCandidate))
            return false;

        return true;
    }
}
