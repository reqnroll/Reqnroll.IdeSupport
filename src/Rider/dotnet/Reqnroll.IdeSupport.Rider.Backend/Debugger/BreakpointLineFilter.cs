namespace Reqnroll.IdeSupport.Rider.Backend.Debugger;

/// <summary>
/// Decides from a line's text alone whether a breakpoint can be worth offering on it. Deliberately
/// knows nothing about Gherkin structure; it only rejects lines that can never contain executable code.
/// </summary>
public static class BreakpointLineFilter
{
    public static bool IsBreakpointCandidate(string lineText)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return false;

        return lineText.TrimStart()[0] != '#';
    }
}
