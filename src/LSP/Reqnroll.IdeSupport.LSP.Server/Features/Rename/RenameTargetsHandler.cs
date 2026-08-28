using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Handles <c>reqnroll/renameTargets</c> — enumerates all binding attributes at the cursor
/// position for the Step Rename feature's multi-attribute picker flow. Extracted from
/// <see cref="StepRenameHandler"/> (issue #139): a distinct LSP custom method with no session
/// state of its own, sharing only the read-only binding-resolution primitives in
/// <see cref="RenameBindingResolver"/>.
/// </summary>
public sealed class RenameTargetsHandler
{
    private readonly IProjectBindingRegistryLookup  _registryLookup;
    private readonly RenameBindingResolver          _bindingResolver;
    private readonly CSharpAttributeLiteralResolver  _attributeLiteralResolver;
    private readonly IOperationDurationRecorder     _recorder;

    internal RenameTargetsHandler(
        IProjectBindingRegistryLookup registryLookup,
        RenameBindingResolver         bindingResolver,
        CSharpAttributeLiteralResolver attributeLiteralResolver,
        IOperationDurationRecorder?   recorder = null)
    {
        _registryLookup           = registryLookup;
        _bindingResolver          = bindingResolver;
        _attributeLiteralResolver = attributeLiteralResolver;
        _recorder                 = recorder ?? NullOperationDurationRecorder.Instance;
    }

    public async Task<RenameTargetsResponse?> HandleRenameTargetsAsync(
        RenameTargetsParams request,
        CancellationToken   cancellationToken)
    {
        var uri  = request.TextDocument.Uri;
        var path = uri.GetFileSystemPath();

        // Performance Verification (Layer 4): time the rename-targets picker resolution.
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollRenameTargets, uri);

        if (string.IsNullOrEmpty(path))
            return null;

        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRenameTargetsFromCSharpAsync(
                uri, path, request.Position, request.RequireAttributeLine, cancellationToken);
        }

        if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRenameTargetsFromFeatureAsync(uri, path, request.Position, cancellationToken);
        }

        return null;
    }

    private async Task<RenameTargetsResponse?> HandleRenameTargetsFromCSharpAsync(
        DocumentUri uri, string path, Position position, bool requireAttributeLine,
        CancellationToken cancellationToken)
    {
        var line = position.Line + 1;

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return new RenameTargetsResponse();

        // Collect all bindings at this method location (heuristic: within 5 lines, unless
        // requireAttributeLine narrows this to the binding's own attribute line — see
        // RenameTargetsParams.RequireAttributeLine).
        var allBindings = RenameBindingResolver.FindBindingsAtCSharpMethod(
            registry, path, line, requireAttributeLine);

        if (allBindings.Count == 0)
            return new RenameTargetsResponse();

        var response = new RenameTargetsResponse();
        int idx = 0;
        foreach (var b in allBindings)
        {
            // Prefer the live source expression (preserves Cucumber parameter types); falls back
            // to null (not b.Expression's raw auto-generated regex) for method-name-style bindings
            // (issue #344) — the Label below omits the expression entirely in that case rather
            // than show a placeholder, since all entries here are attributes on the same method.
            var sourceLiteral = await _attributeLiteralResolver.FindAttributeLiteralAsync(uri, b);
            var expression = sourceLiteral?.Token.ValueText ?? b.DisplayExpression;
            var expressionPart = expression is null ? "" : $" {expression}";

            var scopeTag = b.Scope?.Tag?.ToString();
            var scopeSuffix = !string.IsNullOrEmpty(scopeTag) ? $" [@{scopeTag}]" : "";
            response.Targets.Add(new RenameTargetItem
            {
                Label = $"{b.StepDefinitionType}{expressionPart}{scopeSuffix}",
                Expression = expression ?? "",
                AttributeIndex = idx,
                StartLine = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                StartChar = 1,
                EndLine   = (b.Implementation.SourceLocation?.SourceFileLine ?? line) - 1,
                EndChar   = 200
            });
            idx++;
        }

        return response;
    }

    private async Task<RenameTargetsResponse?> HandleRenameTargetsFromFeatureAsync(
        DocumentUri uri, string path, Position position, CancellationToken cancellationToken)
    {
        var matchedBindings = _bindingResolver.FindBindingsAtFeatureStep(uri, path, position: position);
        if (matchedBindings.Count == 0)
            return new RenameTargetsResponse();

        var response = new RenameTargetsResponse();
        int idx = 0;
        foreach (var b in matchedBindings)
        {
            // Ambiguous bindings from the .feature side are frequently identical steps bound
            // to different methods (that's the whole reason they're ambiguous) — the expression
            // text alone doesn't distinguish them in the picker, so append the implementing
            // method to give the user something to choose by. Implementation.Method is fully
            // qualified (e.g. "MyProj.StepDefinitions.CalculatorSteps.GivenX(Int32)"); the shared
            // namespace prefix across bindings in the same project pushes the actually-different
            // part (class + method name) past the picker's visible width before two entries'
            // labels diverge, so only the last two dot-segments are kept.
            var method = ShortenMethodQualifier(b.Implementation?.Method);
            var methodSuffix = !string.IsNullOrEmpty(method) ? $" — {method}" : "";
            // b.DisplayExpression is null for method-name-style bindings (issue #344) — omit the
            // expression from the Label entirely rather than show a placeholder; methodSuffix
            // (always present here) is what actually identifies the binding in that case.
            var expressionPart = b.DisplayExpression is null ? "" : $" {b.DisplayExpression}";
            response.Targets.Add(new RenameTargetItem
            {
                Label = $"{b.StepDefinitionType}{expressionPart}{methodSuffix}",
                Expression = b.DisplayExpression ?? "",
                AttributeIndex = idx,
                StartLine = 0, StartChar = 0, EndLine = 0, EndChar = 200
            });
            idx++;
        }

        return response;
    }

    /// <summary>
    /// Keeps only the last two dot-segments of a fully qualified method name (class + method),
    /// dropping the namespace. Two ambiguous bindings from the same project usually share the
    /// same namespace prefix, so keeping it just wastes the picker's limited width without
    /// helping the user distinguish the entries.
    /// </summary>
    private static string? ShortenMethodQualifier(string? fullyQualifiedMethod)
    {
        if (string.IsNullOrEmpty(fullyQualifiedMethod))
            return fullyQualifiedMethod;

        var parts = fullyQualifiedMethod.Split('.');
        return parts.Length <= 2 ? fullyQualifiedMethod : string.Join(".", parts[^2..]);
    }
}
