using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Locates and rewrites a step-definition binding's C# attribute string literal via Roslyn.
/// Extracted from <see cref="StepRenameHandler"/> (issue #139) as a self-contained module —
/// "given a binding, find or rewrite its attribute literal" — with no feature-file concerns.
/// </summary>
internal sealed class CSharpAttributeLiteralResolver
{
    private readonly ICSharpFileTextCache   _csharpFileTextCache;
    private readonly IDocumentBufferService _documentBuffer;
    private readonly IIdeSupportLogger      _logger;

    public CSharpAttributeLiteralResolver(
        ICSharpFileTextCache   csharpFileTextCache,
        IDocumentBufferService documentBuffer,
        IIdeSupportLogger      logger)
    {
        _csharpFileTextCache = csharpFileTextCache;
        _documentBuffer      = documentBuffer;
        _logger              = logger;
    }

    /// <summary>
    /// Computes the LSP range of a string literal's inner text, i.e. its <see cref="LiteralExpressionSyntax.Token"/>
    /// span with the surrounding quote characters excluded (2 leading characters for a verbatim
    /// string's <c>@"</c>, 1 otherwise; 1 trailing character for the closing <c>"</c>).
    /// </summary>
    public static LspRange GetLiteralInnerRange(LiteralExpressionSyntax literal)
    {
        var tokenText = literal.Token.Text;
        var leadingQuoteLength = tokenText.StartsWith("@\"", StringComparison.Ordinal) ? 2 : 1;
        const int trailingQuoteLength = 1;

        var fullSpan = literal.Token.Span;
        var innerSpan = new Microsoft.CodeAnalysis.Text.TextSpan(
            fullSpan.Start + leadingQuoteLength,
            fullSpan.Length - leadingQuoteLength - trailingQuoteLength);

        var lineSpan = literal.SyntaxTree!.GetLineSpan(innerSpan);
        var startPos = lineSpan.StartLinePosition;
        var endPos   = lineSpan.EndLinePosition;

        return new LspRange
        {
            Start = new Position(startPos.Line, startPos.Character),
            End   = new Position(endPos.Line, endPos.Character)
        };
    }

    public TextEdit? BuildEdit(
        LiteralExpressionSyntax? literalArgument,
        string newName)
    {
        if (literalArgument == null)
        {
            _logger.LogVerbose("CSharpAttributeLiteralResolver: BuildEdit — no attribute literal found");
            return null;
        }

        // Preserve the parameter tokens as written in the source. The rename dialog edits
        // the non-parameter text only; the parameter slots must keep their original syntax
        // (e.g. a Cucumber '{int}' stays '{int}', a regex '(.*)' stays '(.*)') rather than
        // whatever projection the dialog happened to seed.
        var sourceExpression = literalArgument.Token.ValueText;
        var finalText = ReconcileParameterTokens(sourceExpression, newName);

        // Convert the character-offset TextSpan to line/column using the SyntaxTree
        var lineSpan = literalArgument.SyntaxTree!.GetLineSpan(literalArgument.Token.Span);
        var startPos = lineSpan.StartLinePosition;
        var endPos   = lineSpan.EndLinePosition;

        _logger.LogVerbose($"CSharpAttributeLiteralResolver: BuildEdit — returning edit at ({startPos.Line},{startPos.Character})-({endPos.Line},{endPos.Character}): '{finalText}'");

        return new TextEdit
        {
            Range = new LspRange
            {
                Start = new Position(startPos.Line, startPos.Character),
                End   = new Position(endPos.Line, endPos.Character)
            },
            NewText = "\"" + finalText + "\""
        };
    }

    /// <summary>
    /// Resolves the string-literal attribute argument for <paramref name="binding"/> by its
    /// SOURCE LOCATION, not by matching the registry's expression text. The registry
    /// expression is a discovery-time projection (a Cucumber expression is rendered to a regex
    /// during discovery, and it reflects the last compiled build rather than the live buffer),
    /// so it cannot be relied on to equal the raw attribute string literal.
    /// </summary>
    /// <remarks>
    /// When <paramref name="binding"/> carries <see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/>
    /// (syntax-discovered bindings — the AST-derived exact attribute line, using the identical
    /// formula <see cref="Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp.StepDefinitionFileParser"/>
    /// used to populate it), that line is matched exactly first — unambiguous even when a method
    /// carries several same-type attributes, which a "nearest method" search alone cannot
    /// distinguish (the same bug class fixed for the rename-targets picker in
    /// <see cref="RenameBindingResolver.FindBindingsAtCSharpMethod"/>, #170). Only when no exact
    /// match is found (a stale build's recorded line has drifted from the live buffer, or the
    /// binding is connector-discovered and never carried an attribute line at all) does this fall
    /// back to the previous "nearest candidate method" tolerance.
    /// </remarks>
    public async Task<LiteralExpressionSyntax?> FindAttributeLiteralAsync(
        DocumentUri uri,
        ProjectStepDefinitionBinding binding)
    {
        var csPath = ResolveCSharpFilePath(uri, binding);
        if (csPath == null)
            return null;

        var csUri = string.Equals(uri.GetFileSystemPath(), csPath, StringComparison.OrdinalIgnoreCase)
            ? uri
            : DocumentUri.FromFileSystemPath(csPath);

        var fileText = await AcquireFileTextAsync(csUri, csPath).ConfigureAwait(false);
        if (fileText == null)
        {
            _logger.LogVerbose("CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — no file text available");
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(fileText);
        var rootNode = await tree.GetRootAsync();

        return FindAttributeLiteral(tree, rootNode, binding);
    }

    /// <summary>
    /// Resolves the <c>.cs</c> file path to parse for <paramref name="binding"/>: the request
    /// URI itself when it's already a <c>.cs</c> path, otherwise (a <c>.feature</c>-triggered
    /// rename, or a request URI the server couldn't resolve to a filesystem path) the binding's
    /// own recorded source file. Returns <see langword="null"/> when neither is available.
    /// </summary>
    private string? ResolveCSharpFilePath(DocumentUri uri, ProjectStepDefinitionBinding binding)
    {
        var csPath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(csPath))
        {
            if (binding?.Implementation?.SourceLocation?.SourceFile == null)
            {
                _logger.LogVerbose("CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — csPath is null/empty");
                return null;
            }

            csPath = binding.Implementation.SourceLocation.SourceFile;
            _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — using binding source file '{csPath}'");
            return csPath;
        }

        if (!csPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // When called from a .feature file, use the binding's C# source file
            if (binding?.Implementation?.SourceLocation?.SourceFile == null)
            {
                _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — non-cs file and no binding source: '{csPath}'");
                return null;
            }

            var redirected = binding.Implementation.SourceLocation.SourceFile;
            _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — redirected from '{csPath}' to binding source '{redirected}'");
            return redirected;
        }

        return csPath;
    }

    /// <summary>
    /// Acquires the current text of <paramref name="csPath"/>: the live <c>.cs</c> text cache
    /// (updated by every didOpen/didChange for this file, from any source — not just our own
    /// rename edits, see <see cref="ICSharpFileTextCache"/>), the Gherkin document buffer (never
    /// actually populated for <c>.cs</c> paths — kept in case that ever changes), or disk as a
    /// last resort. Without the cache, a <c>.cs</c> edit applied via <c>workspace/applyEdit</c>
    /// is never saved to disk, so re-invoking rename on the same step before saving would
    /// silently read the pre-edit text back off disk and show a stale placeholder (confirmed live).
    /// </summary>
    private async Task<string?> AcquireFileTextAsync(DocumentUri csUri, string csPath)
    {
        if (_csharpFileTextCache.TryGet(csUri, out var cachedText) && cachedText != null)
        {
            _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — got text from live cache ({cachedText.Length} chars)");
            return cachedText;
        }

        if (_documentBuffer.TryGet(csUri, out var buffer) && buffer?.Text != null)
        {
            _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — got text from buffer ({buffer.Text.Length} chars)");
            return buffer.Text;
        }

        if (System.IO.File.Exists(csPath))
        {
            var text = await System.IO.File.ReadAllTextAsync(csPath);
            _logger.LogVerbose($"CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — got text from disk ({text.Length} chars)");
            return text;
        }

        return null;
    }

    /// <summary>
    /// Finds the literal to rewrite within an already-parsed tree: an exact
    /// <see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/> match first (unambiguous
    /// even when a method carries several same-type attributes, which a "nearest method" search
    /// alone cannot distinguish — the same bug class fixed for the rename-targets picker in
    /// <see cref="RenameBindingResolver.FindBindingsAtCSharpMethod"/>, #170), falling back to the
    /// nearest candidate method when no exact match is found (a stale build's recorded line has
    /// drifted from the live buffer, or the binding is connector-discovered and never carried an
    /// attribute line at all).
    /// </summary>
    private LiteralExpressionSyntax? FindAttributeLiteral(
        SyntaxTree tree, SyntaxNode rootNode, ProjectStepDefinitionBinding binding)
    {
        var stepType = binding.StepDefinitionType;
        var methods = rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (binding.AttributeSourceLine.HasValue)
        {
            var exact = methods
                .SelectMany(m => GetStepAttributesWithLiterals(m, stepType))
                .FirstOrDefault(x =>
                    x.Attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                        == binding.AttributeSourceLine.Value);
            if (exact.Literal != null)
                return exact.Literal;
        }

        var candidates = methods
            .Select(m => (Method: m,
                          Line: tree.GetLineSpan(m.Identifier.Span).StartLinePosition.Line + 1)) // 1-based
            .Where(x => GetStepAttributeLiterals(x.Method, stepType).Any())
            .ToList();

        if (candidates.Count == 0)
            return null;

        var targetLine = binding.Implementation?.SourceLocation?.SourceFileLine;
        var chosen = targetLine.HasValue
            ? candidates.OrderBy(x => Math.Abs(x.Line - targetLine.Value)).ThenBy(x => x.Line).First()
            : candidates.First();

        // Among the chosen method's matching step attributes, pick the literal to rewrite.
        // A single matching attribute (the common case) is selected regardless of its text.
        // When a method carries several same-type attributes, prefer the one whose literal
        // equals the registry expression, falling back to the first.
        var literals = GetStepAttributeLiterals(chosen.Method, stepType).ToList();
        return literals.FirstOrDefault(e => e.Token.ValueText == binding.Expression)
               ?? literals[0];
    }

    /// <summary>
    /// Rebuilds <paramref name="newExpression"/> so that its parameter slots carry the exact
    /// tokens from <paramref name="sourceExpression"/> (positionally). This keeps the original
    /// parameter syntax — a Cucumber <c>{int}</c> stays <c>{int}</c>, a regex <c>(.*)</c> stays
    /// <c>(.*)</c> — even when the rename dialog seeded a different projection. The user's edits
    /// to the non-parameter text are preserved. When the slot counts differ, the user's text is
    /// honoured verbatim.
    /// </summary>
    internal static string ReconcileParameterTokens(string sourceExpression, string newExpression)
    {
        var originalSlots = StepExpressionParameters.ExtractSlots(sourceExpression);
        if (originalSlots.Count == 0)
            return newExpression;

        var newSlots = StepExpressionParameters.ExtractSlots(newExpression);
        if (newSlots.Count != originalSlots.Count)
            return newExpression;

        var sb = new System.Text.StringBuilder();
        var slotIndex = 0;
        var i = 0;
        while (i < newExpression.Length)
        {
            var slotLength = StepExpressionParameters.SlotLengthAt(newExpression, i);
            if (slotLength > 0)
            {
                sb.Append(originalSlots[slotIndex]);
                slotIndex++;
                i += slotLength;
            }
            else
            {
                sb.Append(newExpression[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the first string-literal argument of every attribute on <paramref name="method"/>
    /// that is a step-definition attribute for <paramref name="stepType"/> (<c>Given</c>/<c>When</c>/
    /// <c>Then</c>, or <c>StepDefinition</c> which applies to all step kinds).
    /// </summary>
    private static IEnumerable<LiteralExpressionSyntax> GetStepAttributeLiterals(
        MethodDeclarationSyntax method, ScenarioBlock stepType) =>
        GetStepAttributesWithLiterals(method, stepType).Select(x => x.Literal);

    /// <summary>
    /// Same as <see cref="GetStepAttributeLiterals"/>, but keeps each literal paired with its
    /// enclosing <see cref="AttributeSyntax"/> so the caller can compute the attribute's own
    /// source line (needed to match <see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/>
    /// exactly, which a method-level line alone cannot do when several same-type attributes share
    /// one method).
    /// </summary>
    private static IEnumerable<(AttributeSyntax Attribute, LiteralExpressionSyntax Literal)> GetStepAttributesWithLiterals(
        MethodDeclarationSyntax method, ScenarioBlock stepType)
    {
        foreach (var attr in method.AttributeLists.SelectMany(al => al.Attributes))
        {
            if (!IsStepAttributeFor(attr, stepType))
                continue;

            var literal = attr.ArgumentList?.Arguments
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(e => e.RawKind == (int)SyntaxKind.StringLiteralExpression);

            if (literal != null)
                yield return (attr, literal);
        }
    }

    private static bool IsStepAttributeFor(AttributeSyntax attr, ScenarioBlock stepType)
    {
        var name = attr.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            SimpleNameSyntax    s => s.Identifier.Text,
            _                     => attr.Name.ToString()
        };

        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "Attribute".Length);

        // [StepDefinition("…")] registers for Given/When/Then alike.
        if (string.Equals(name, "StepDefinition", StringComparison.Ordinal))
            return true;

        return stepType switch
        {
            ScenarioBlock.Given => name == "Given",
            ScenarioBlock.When  => name == "When",
            ScenarioBlock.Then  => name == "Then",
            _                   => name is "Given" or "When" or "Then"
        };
    }
}
