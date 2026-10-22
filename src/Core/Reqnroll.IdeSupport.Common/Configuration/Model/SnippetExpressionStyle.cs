namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>SnippetExpressionStyle</summary>
public enum SnippetExpressionStyle
{
    /// <summary>Step definition snippets use a plain, synchronous regular expression attribute.</summary>
    RegularExpression,
    /// <summary>Step definition snippets use a synchronous Cucumber Expression attribute.</summary>
    CucumberExpression,
    /// <summary>Step definition snippets use an async regular expression attribute.</summary>
    AsyncRegularExpression,
    /// <summary>Step definition snippets use an async Cucumber Expression attribute.</summary>
    AsyncCucumberExpression
}

/// <summary>SnippetExpressionStyleExtensions</summary>
public static class SnippetExpressionStyleExtensions
{
    /// <summary>Determines whether the given snippet style generates an async step definition.</summary>
    public static bool IsAsync(this SnippetExpressionStyle style)
    {
        if (style == SnippetExpressionStyle.AsyncRegularExpression
            || style == SnippetExpressionStyle.AsyncCucumberExpression)
            return true;
        return false;
    }

    /// <summary>Determines whether the given snippet style uses a Cucumber Expression rather than a regular expression.</summary>
    public static bool IsCucumber(this SnippetExpressionStyle style)
    {
        if (style == SnippetExpressionStyle.CucumberExpression
            || style == SnippetExpressionStyle.AsyncCucumberExpression)
            return true;
        return false;
    }
}