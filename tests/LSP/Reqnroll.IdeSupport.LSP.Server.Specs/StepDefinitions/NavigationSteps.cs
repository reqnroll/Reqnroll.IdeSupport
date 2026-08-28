using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class NavigationSteps
{
    private readonly LspScenarioContext _ctx;

    public NavigationSteps(LspScenarioContext ctx) => _ctx = ctx;

    [When(@"references are requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenReferencesAreRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastReferences = await _ctx.Harness.Client
            .RequestReferencesAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"(\d+) reference(?:s are|s is| is| are) returned")]
    public void ThenNReferencesAreReturned(int expected)
    {
        if (expected == 0)
        {
            (_ctx.LastReferences is null || !_ctx.LastReferences.Any())
                .Should().BeTrue($"expected 0 references but got {_ctx.LastReferences?.Count()}");
        }
        else
        {
            _ctx.LastReferences.Should().NotBeNull("references should have been returned");
            _ctx.LastReferences!.Count().Should().Be(expected);
        }
    }

    [Then(@"the references include a location in ""(.*)""")]
    public void ThenTheReferencesIncludeALocationIn(string fileName)
    {
        _ctx.LastReferences.Should().NotBeNull("references should have been returned");
        _ctx.LastReferences!.Should().Contain(
            loc => loc.Location!.Uri.ToString()
                       .EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a reference to '{fileName}' should be present");
    }

    // ── reqnroll/findStepUsages (three-state custom request) ──────────────────

    [When(@"step usages are requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenStepUsagesAreRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFindStepUsages = await _ctx.Harness.Client
            .RequestFindStepUsagesAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"the step usages response has isBinding false")]
    public void ThenStepUsagesResponseIsNotBinding()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.IsBinding.Should().BeFalse(
            "isBinding=false signals 'not a binding' — client should fall through to built-in C# FAR");
    }

    [Then(@"the step usages response has isBinding true")]
    public void ThenStepUsagesResponseIsBinding()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull("server should return a response for a binding position");
        _ctx.LastFindStepUsages!.IsBinding.Should().BeTrue();
    }

    [Then(@"(\d+) step usage(?:s are|s is| is| are) returned")]
    public void ThenNStepUsagesAreReturned(int expected)
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().HaveCount(expected);
    }

    [Then(@"the step usages include a location in ""(.*)""")]
    public void ThenStepUsagesIncludeALocationIn(string fileName)
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().Contain(
            loc => loc.Uri.EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a step usage in '{fileName}' should be present");
    }

    [Then(@"the step usages include a non-empty step text")]
    public void ThenStepUsagesIncludeNonEmptyStepText()
    {
        _ctx.LastFindStepUsages.Should().NotBeNull();
        _ctx.LastFindStepUsages!.Locations.Should().Contain(
            loc => !string.IsNullOrWhiteSpace(loc.StepText),
            "at least one location should carry step text extracted from the in-memory snapshot");
    }

    // ── reqnroll/goToHooks (F17 — Hook Navigation) ────────────────────────────

    [When(@"go to hooks is requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenGoToHooksIsRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastGoToHooks = await _ctx.Harness.Client
            .RequestGoToHooksAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"(\d+) hook result(?:s are|s is| is| are) returned")]
    public void ThenNHookResultsAreReturned(int expected)
    {
        _ctx.LastGoToHooks.Should().NotBeNull("the server should return a GoToHooksResponse");
        _ctx.LastGoToHooks!.Hooks.Should().HaveCount(expected);
    }

    [Then(@"the hook results include a ""(.*)"" hook")]
    public void ThenHookResultsIncludeHookType(string hookType)
    {
        _ctx.LastGoToHooks.Should().NotBeNull();
        _ctx.LastGoToHooks!.Hooks.Should().Contain(
            h => h.HookType == hookType,
            $"a '{hookType}' hook should be present in the results");
    }

    [Then(@"the hook results include a location in ""(.*)""")]
    public void ThenHookResultsIncludeLocationIn(string fileName)
    {
        _ctx.LastGoToHooks.Should().NotBeNull();
        _ctx.LastGoToHooks!.Hooks.Should().Contain(
            h => h.Uri.EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a hook with a location in '{fileName}' should be present");
    }

    // ── textDocument/codeLens (F18 — Step Code Lens) ──────────────────────────

    [When(@"code lens is requested for ""(.*)""")]
    public async Task WhenCodeLensIsRequestedFor(string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastCodeLens = await _ctx.Harness.Client
            .RequestCodeLensAsync(uri)
            .ConfigureAwait(false);
    }

    [Then(@"(\d+) code lens(?:es are|es is| is| are) returned")]
    public void ThenNCodeLensesAreReturned(int expected)
    {
        if (expected == 0)
        {
            var count = _ctx.LastCodeLens?.Length ?? 0;
            count.Should().Be(0, $"expected 0 code lenses but got {count}");
        }
        else
        {
            _ctx.LastCodeLens.Should().NotBeNull("code lenses should have been returned");
            _ctx.LastCodeLens!.Should().HaveCount(expected);
        }
    }

    [Then(@"the code lens at index (\d+) has title ""(.*)""")]
    public async Task ThenCodeLensAtIndexHasTitle(int index, string expectedTitle)
    {
        _ctx.LastCodeLens.Should().NotBeNull();
        _ctx.LastCodeLens!.Should().HaveCountGreaterThan(index,
            $"at least {index + 1} code lenses should exist");
        var lens = await ResolveIfDeferredAsync(_ctx.LastCodeLens![index]).ConfigureAwait(false);
        lens.Command!.Title.Should().Be(expectedTitle);
    }

    [Then(@"at least one code lens has a title containing ""(.*)""")]
    public async Task ThenAtLeastOneCodeLensHasTitleContaining(string fragment)
    {
        _ctx.LastCodeLens.Should().NotBeNull();
        var resolved = await ResolveAllIfDeferredAsync(_ctx.LastCodeLens!).ConfigureAwait(false);
        resolved.Should().Contain(
            lens => lens.Command != null &&
                    lens.Command.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase),
            $"at least one code lens should have a title containing '{fragment}'");
    }

    [Then(@"all code lenses have command ""(.*)""")]
    public async Task ThenAllCodeLensesHaveCommand(string commandName)
    {
        _ctx.LastCodeLens.Should().NotBeNull();
        var resolved = await ResolveAllIfDeferredAsync(_ctx.LastCodeLens!).ConfigureAwait(false);
        resolved.Should().OnlyContain(
            lens => lens.Command != null && lens.Command.Name == commandName,
            $"all code lenses should have command '{commandName}'");
    }

    /// <summary>
    /// Follows up with <c>codeLens/resolve</c> (issue #471) for a lens whose <c>Command</c> came
    /// back unset from <c>textDocument/codeLens</c>, exactly as a spec-compliant client would.
    /// <para>
    /// This is a passthrough in practice today: the server only hands out unresolved placeholder
    /// lenses to clients on <c>ClientIdeContext</c>'s <c>codeLens/resolve</c> allowlist, and that
    /// allowlist is empty, so every lens these specs see already carries its <c>Command</c>. The
    /// helper stays because it makes these assertions correct for EITHER server behaviour — so
    /// they keep passing unchanged the day a client is allowlisted and the deferred path goes
    /// live, instead of silently asserting against a null <c>Command</c>.
    /// </para>
    /// </summary>
    private async Task<CodeLens> ResolveIfDeferredAsync(CodeLens lens)
    {
        if (lens.Command is not null) return lens;
        var resolved = await _ctx.Harness.Client.RequestCodeLensResolveAsync(lens).ConfigureAwait(false);
        resolved.Should().NotBeNull("codeLens/resolve should always return a lens");
        return resolved!;
    }

    private async Task<CodeLens[]> ResolveAllIfDeferredAsync(IEnumerable<CodeLens> lenses)
    {
        var results = new List<CodeLens>();
        foreach (var lens in lenses)
            results.Add(await ResolveIfDeferredAsync(lens).ConfigureAwait(false));
        return results.ToArray();
    }

}
