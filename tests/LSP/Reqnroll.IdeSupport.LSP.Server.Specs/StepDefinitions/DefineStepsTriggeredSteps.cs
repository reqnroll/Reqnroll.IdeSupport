using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class DefineStepsTriggeredSteps
{
    private readonly LspScenarioContext _ctx;
    private Exception? _error;

    public DefineStepsTriggeredSteps(LspScenarioContext ctx) => _ctx = ctx;

    [When("the Define Steps trigger command is executed for \"(.*)\"")]
    public async Task WhenTheDefineStepsTriggerCommandIsExecuted(string fileName)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        try
        {
            await _ctx.Harness.Client.RequestCommandAsync(new ExecuteCommandParams
            {
                Command   = "reqnroll.defineStepsTriggered",
                Arguments = new JArray(_ctx.UriFor(fileName).ToString())
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = ex;
        }
    }

    [Then("the command completes without an error")]
    public void ThenTheCommandCompletesWithoutAnError() =>
        _error.Should().BeNull("the command is registered by DefineStepsTriggeredHandler");

    [Then("no applyEdit request is sent")]
    public void ThenNoApplyEditIsSent() =>
        _ctx.Harness.LastApplyEdit.Should().BeNull();
}
