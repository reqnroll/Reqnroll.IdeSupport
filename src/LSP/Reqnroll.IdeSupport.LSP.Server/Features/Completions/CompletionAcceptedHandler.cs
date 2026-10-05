using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Completions;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <see cref="CommandName"/>, the
/// <see cref="CompletionItem.Command"/> attached to step completion items, so the server learns a
/// step completion was accepted (issue #883 spike prototype). Increments
/// <see cref="FeatureUsageCatalog.StepCompletionAcceptedKey"/>; sends no event and carries no
/// arguments, so no user data can reach the counter.
/// </summary>
public sealed class CompletionAcceptedHandler : IExecuteCommandHandler
{
    /// <summary>The command name carried by every step completion item.</summary>
    public const string CommandName = "reqnroll.completionAccepted";

    private readonly IFeatureUsageCounters? _counters;

    /// <summary>Initializes a new instance of the <see cref="CompletionAcceptedHandler"/> class.</summary>
    public CompletionAcceptedHandler(IFeatureUsageCounters? counters = null) => _counters = counters;

    /// <summary>Advertises <see cref="CommandName"/> as an executable <c>workspace/executeCommand</c> command.</summary>
    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new() { Commands = new Container<string>(CommandName) };

    /// <summary>Counts the accepted step completion.</summary>
    public Task<Unit> Handle(ExecuteCommandParams request, CancellationToken cancellationToken)
    {
        if (request.Command == CommandName)
            _counters?.Increment(FeatureUsageCatalog.StepCompletionAcceptedKey);

        return Unit.Task;
    }
}
