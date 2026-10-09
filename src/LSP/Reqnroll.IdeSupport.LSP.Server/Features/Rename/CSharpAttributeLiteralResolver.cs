using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Locates and rewrites a step-definition binding's C# attribute string literal via Roslyn.
/// Extracted from <see cref="RenameHandler"/> (issue #139) as a self-contained module —
/// "given a binding, find or rewrite its attribute literal" — with no feature-file concerns.
/// </summary>
internal sealed class CSharpAttributeLiteralResolver
{
    private readonly ICSharpFileTextCache   _csharpFileTextCache;
    private readonly IDocumentBufferService _documentBuffer;
    private readonly IIdeSupportLogger      _logger;
    private readonly IFileSystemForIDE      _fileSystem;
    private readonly ICSharpSyntaxTreeCache _syntaxTreeCache;

    /// <summary>Initializes a new instance of the <see cref="CSharpAttributeLiteralResolver"/> class.
    /// <paramref name="syntaxTreeCache"/> defaults when omitted so existing direct-construction call
    /// sites (including tests) keep working unchanged; production wiring supplies the shared,
    /// DI-registered cache instance.</summary>
    public CSharpAttributeLiteralResolver(
        ICSharpFileTextCache   csharpFileTextCache,
        IDocumentBufferService documentBuffer,
        IIdeSupportLogger      logger,
        IFileSystemForIDE      fileSystem,
        ICSharpSyntaxTreeCache? syntaxTreeCache = null)
    {
        _csharpFileTextCache = csharpFileTextCache;
        _documentBuffer      = documentBuffer;
        _logger              = logger;
        _fileSystem          = fileSystem;
        _syntaxTreeCache     = syntaxTreeCache ?? new CSharpSyntaxTreeCache();
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
        ProjectStepDefinitionBinding binding) =>
        (await ResolveAttributeLiteralAsync(uri, binding).ConfigureAwait(false)).Literal;

    /// <summary>
    /// The outcome of <see cref="ResolveAttributeLiteralAsync"/>: the attribute literal to rewrite,
    /// or <see langword="null"/>. <see cref="IsAmbiguous"/> distinguishes "the method carries
    /// several matching attributes and none can be identified as this binding's" (issue #940) —
    /// which a rename must refuse rather than guess at — from "no literal found".
    /// </summary>
    public readonly record struct AttributeLiteralResolution(LiteralExpressionSyntax? Literal, bool IsAmbiguous)
    {
        public static readonly AttributeLiteralResolution NotFound  = new(null, IsAmbiguous: false);
        public static readonly AttributeLiteralResolution Ambiguous = new(null, IsAmbiguous: true);

        public static AttributeLiteralResolution Found(LiteralExpressionSyntax literal) => new(literal, IsAmbiguous: false);
    }

    /// <summary>
    /// Same as <see cref="FindAttributeLiteralAsync"/>, but reports when the literal could not be
    /// chosen among several same-type attributes on the binding's method (issue #940), so the
    /// rename can fail with an explanation instead of rewriting an attribute that may not be this
    /// binding's.
    /// </summary>
    public async Task<AttributeLiteralResolution> ResolveAttributeLiteralAsync(
        DocumentUri uri,
        ProjectStepDefinitionBinding binding)
    {
        // A method-name-style binding (issue #344) has no string-literal attribute argument
        // anywhere, by definition — [Given] with no expression. Without this early exit,
        // FindAttributeLiteral's "nearest candidate method" fallback below has nothing of this
        // binding's own to find and silently snaps to a geometrically nearby *unrelated* method
        // that does have a literal, misattributing that method's expression to this rename.
        if (binding.IsMethodNameStyle)
        {
            _logger.LogVerbose(
                "CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — method-name-style binding has no literal to find");
            return AttributeLiteralResolution.NotFound;
        }

        var csPath = ResolveCSharpFilePath(uri, binding);
        if (csPath == null)
            return AttributeLiteralResolution.NotFound;

        var csUri = string.Equals(uri.GetFileSystemPath(), csPath, StringComparison.OrdinalIgnoreCase)
            ? uri
            : DocumentUri.FromFileSystemPath(csPath);

        var fileText = await AcquireFileTextAsync(csUri, csPath).ConfigureAwait(false);
        if (fileText == null)
        {
            _logger.LogVerbose("CSharpAttributeLiteralResolver: FindAttributeLiteralAsync — no file text available");
            return AttributeLiteralResolution.NotFound;
        }

        // Cached across calls within the same rename operation (issue #491): a method carrying
        // several step-definition attributes resolves each one through this same call, which used
        // to re-parse the file from scratch every time. GetOrParse's text-equality check keeps
        // this correct against a concurrent edit to the same file.
        var rootNode = _syntaxTreeCache.GetOrParse(csPath, fileText);
        var tree = rootNode.SyntaxTree;

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

        if (_fileSystem.File.Exists(csPath))
        {
            var text = await _fileSystem.File.ReadAllTextAsync(csPath);
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
    /// attribute line at all). Either way, the method's attribute literals are then narrowed by
    /// <see cref="SelectAmongMethodLiterals"/> (issue #940).
    /// </summary>
    private AttributeLiteralResolution FindAttributeLiteral(
        SyntaxTree tree, SyntaxNode rootNode, ProjectStepDefinitionBinding binding)
    {
        var stepType = binding.StepDefinitionType;
        var methods = rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (binding.AttributeSourceLine.HasValue)
        {
            var onLine = methods
                .SelectMany(m => GetStepAttributesWithLiterals(m, stepType).Select(x => (Method: m, x.Attribute, x.Literal)))
                .Where(x =>
                    x.Attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                        == binding.AttributeSourceLine.Value)
                .ToList();
            if (onLine.Count > 0)
            {
                // Several attributes written on one line give the line no deciding power.
                var lineMatch = onLine.Count == 1 ? onLine[0].Literal : null;
                return SelectAmongMethodLiterals(
                    GetStepAttributeLiterals(onLine[0].Method, stepType).ToList(), binding, lineMatch);
            }
        }

        var candidates = methods
            .Select(m => (Method: m,
                          Line: tree.GetLineSpan(m.Identifier.Span).StartLinePosition.Line + 1)) // 1-based
            .Where(x => GetStepAttributeLiterals(x.Method, stepType).Any())
            .ToList();

        if (candidates.Count == 0)
            return AttributeLiteralResolution.NotFound;

        var targetLine = binding.Implementation?.SourceLocation?.SourceFileLine;
        var chosen = targetLine.HasValue
            ? candidates.OrderBy(x => Math.Abs(x.Line - targetLine.Value)).ThenBy(x => x.Line).First()
            : candidates.First();

        return SelectAmongMethodLiterals(
            GetStepAttributeLiterals(chosen.Method, stepType).ToList(), binding, lineMatch: null);
    }

    /// <summary>
    /// Picks which of one method's same-type attribute literals belongs to <paramref name="binding"/>,
    /// refusing (<see cref="AttributeLiteralResolution.Ambiguous"/>) rather than guessing when the
    /// evidence does not single one out (issue #940). It used to fall back to the method's first
    /// literal, which rewrote the wrong attribute whenever the registry expression did not equal the
    /// binding's own literal.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>A single literal is selected regardless of its text (the common case; its text may
    /// differ only because the buffer was edited since discovery).</item>
    /// <item>A literal whose text equals the registry expression identifies the binding. When
    /// <paramref name="lineMatch"/> (the literal on the binding's
    /// <see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/>) is not among those, the text
    /// wins: a connector-discovered binding's attribute line is backfilled with the method's FIRST
    /// matching attribute for every binding on the method
    /// (<c>BindingImporter.TryGetAttributeSourceLine</c>), so that line cannot tell them apart.</item>
    /// <item>Otherwise the registry expression may be a projection of the source (a Cucumber
    /// expression's regex form, where <c>{int}</c> became <c>(.*)</c>), so literals are compared by
    /// their static text around the parameter slots. The line match stands when it agrees or when
    /// nothing matches (its literal was edited since discovery); without a line match a single
    /// match is used; anything else is ambiguous.</item>
    /// </list>
    /// </remarks>
    private AttributeLiteralResolution SelectAmongMethodLiterals(
        IReadOnlyList<LiteralExpressionSyntax> literals,
        ProjectStepDefinitionBinding binding,
        LiteralExpressionSyntax? lineMatch)
    {
        if (literals.Count == 0)
            return AttributeLiteralResolution.NotFound;
        if (literals.Count == 1)
            return AttributeLiteralResolution.Found(literals[0]);

        var expression = binding.Expression;

        var exactMatches = literals.Where(l => l.Token.ValueText == expression).ToList();
        if (exactMatches.Count > 0)
        {
            if (lineMatch != null && exactMatches.Contains(lineMatch))
                return AttributeLiteralResolution.Found(lineMatch);
            return exactMatches.Count == 1
                ? AttributeLiteralResolution.Found(exactMatches[0])
                : ReportAmbiguous(literals, binding);
        }

        var shapeMatches = literals.Where(l => HasSameStaticText(l.Token.ValueText, expression)).ToList();
        if (lineMatch != null)
        {
            return shapeMatches.Count == 0 || shapeMatches.Contains(lineMatch)
                ? AttributeLiteralResolution.Found(lineMatch)
                : ReportAmbiguous(literals, binding);
        }

        return shapeMatches.Count == 1
            ? AttributeLiteralResolution.Found(shapeMatches[0])
            : ReportAmbiguous(literals, binding);
    }

    private AttributeLiteralResolution ReportAmbiguous(
        IReadOnlyList<LiteralExpressionSyntax> literals, ProjectStepDefinitionBinding binding)
    {
        _logger.LogWarning(
            $"CSharpAttributeLiteralResolver: cannot tell which of {literals.Count} [{binding.StepDefinitionType}] attributes " +
            $"on '{binding.Implementation?.Method}' belongs to the binding with expression '{binding.Expression}' " +
            $"(source literals: {string.Join(", ", literals.Select(l => $"'{l.Token.ValueText}'"))}); refusing to pick one");
        return AttributeLiteralResolution.Ambiguous;
    }

    /// <summary>
    /// Whether <paramref name="sourceExpression"/> and <paramref name="registryExpression"/> have
    /// the same text around their parameter slots — e.g. <c>the number is {int}</c> and its regex
    /// projection <c>the number is (.*)</c> — ignoring one leading <c>^</c> and trailing <c>$</c>.
    /// </summary>
    private static bool HasSameStaticText(string sourceExpression, string? registryExpression)
    {
        if (registryExpression == null)
            return false;

        return StepExpressionParameters.StaticSegments(TrimAnchors(sourceExpression))
            .SequenceEqual(StepExpressionParameters.StaticSegments(TrimAnchors(registryExpression)), StringComparer.Ordinal);

        static string TrimAnchors(string s)
        {
            if (s.StartsWith("^", StringComparison.Ordinal))
                s = s.Substring(1);
            if (s.EndsWith("$", StringComparison.Ordinal) && !s.EndsWith(@"\$", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1);
            return s;
        }
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
