#nullable enable

using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Builds "Go to '&lt;method&gt;'" navigation <see cref="CodeAction"/>s for an ambiguous step
/// (issue #563) — one per competing step definition, so the lightbulb offers a direct jump to
/// each candidate instead of the empty menu <see cref="CodeActionHandler"/> used to return.
/// </summary>
internal sealed class AmbiguousStepActionBuilder
{
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="AmbiguousStepActionBuilder"/> class.</summary>
    public AmbiguousStepActionBuilder(IFileSystemForIDE fileSystem) => _fileSystem = fileSystem;

    /// <summary>
    /// Builds one navigation action per competing binding that resolves to a local file. A
    /// binding whose source file cannot be found on this machine (see
    /// <see cref="Documents.SourceLocation.IsResolved"/>) contributes no action — same rule
    /// <see cref="Definition.DefinitionHandler"/> applies for the same reason (issue #540).
    /// </summary>
    public List<CommandOrCodeAction> Build(StepBindingMatch ambiguousStep)
    {
        var diagnostic = DiagnosticsPublishHandler.ToLspDiagnostic(new GherkinDiagnostic(
            ambiguousStep.Result.GetErrorMessage() ?? DiagnosticsAggregator.AmbiguousStepMessage,
            ambiguousStep.Range,
            GherkinDiagnosticSeverity.Error,
            DiagnosticsAggregator.BindingSource));
        var diagnostics = new Container<Diagnostic>(diagnostic);

        var actions = new List<CommandOrCodeAction>();
        foreach (var item in ambiguousStep.Result.Items)
        {
            if (item.Type != MatchResultType.Ambiguous)
                continue;

            var impl = item.MatchedStepDefinition?.Implementation;
            var sourceLocation = impl?.SourceLocation;
            if (sourceLocation is null || string.IsNullOrEmpty(sourceLocation.SourceFile) || !sourceLocation.IsResolved)
                continue;

            var location = sourceLocation.WithIdentifierLocation(impl!.Method, _fileSystem).ToLspLocation();
            var fileName = Path.GetFileName(sourceLocation.SourceFile);

            actions.Add(new CommandOrCodeAction(new CodeAction
            {
                Title       = $"Go to '{impl.Method}' ({fileName})",
                Kind        = CodeActionKind.QuickFix,
                Diagnostics = diagnostics,
                // No Edit: this action is pure navigation. VS Code executes this command to open
                // the file at the identifier; other clients receive an unknown command they can
                // safely ignore, the same graceful-degrade pattern DefineStepsActionBuilder uses.
                Command = new Command
                {
                    Title     = "Go to step definition",
                    Name      = "vscode.open",
                    Arguments = new JArray(location.Uri.ToString(), SelectionOptions(location.Range))
                }
            }));
        }

        return actions;
    }

    private static JObject SelectionOptions(LspRange range) => new()
    {
        ["selection"] = new JObject
        {
            ["start"] = new JObject { ["line"] = range.Start.Line, ["character"] = range.Start.Character },
            ["end"]   = new JObject { ["line"] = range.End.Line,   ["character"] = range.End.Character }
        }
    };
}
