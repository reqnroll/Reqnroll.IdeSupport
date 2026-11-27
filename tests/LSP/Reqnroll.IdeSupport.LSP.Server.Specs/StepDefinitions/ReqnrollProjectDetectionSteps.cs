using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>Observes the server's <c>reqnroll/reqnrollProjectDetected</c> notification.</summary>
[Binding]
public sealed class ReqnrollProjectDetectionSteps
{
    // The notification has no response to wait on, so absence is checked after a settle period.
    private const int SettleMs = 1000;

    private readonly LspScenarioContext _ctx;

    public ReqnrollProjectDetectionSteps(LspScenarioContext ctx) => _ctx = ctx;

    [Then("the client is not told that a Reqnroll project was detected")]
    public async Task ThenTheClientIsNotTold()
    {
        await Task.Delay(SettleMs).ConfigureAwait(false);
        Assert.Equal(0, _ctx.Harness.ReqnrollProjectDetectedCount);
    }

    [Then("the client is told once that a Reqnroll project was detected")]
    public async Task ThenTheClientIsToldOnce()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_ctx.Harness.ReqnrollProjectDetectedCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);

        await Task.Delay(SettleMs).ConfigureAwait(false);
        Assert.Equal(1, _ctx.Harness.ReqnrollProjectDetectedCount);
    }
}
