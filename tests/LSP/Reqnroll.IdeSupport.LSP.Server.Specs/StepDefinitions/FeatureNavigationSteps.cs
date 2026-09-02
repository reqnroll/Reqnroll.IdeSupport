using AwesomeAssertions;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Steps for the four feature-level requests driven from a document rather than from the binding
/// registry alone: <c>textDocument/definition</c> (F5), <c>textDocument/inlayHint</c> (F23),
/// <c>reqnroll/resolveTestTargets</c> (F26) and <c>reqnroll/goToMatchingScenarios</c> (F24).
/// </summary>
[Binding]
public sealed class FeatureNavigationSteps
{
    private readonly LspScenarioContext _ctx;

    public FeatureNavigationSteps(LspScenarioContext ctx) => _ctx = ctx;

    // ── F5 · textDocument/definition ────────────────────────────────────────────

    [When(@"go to definition is requested at line (\d+) column (\d+) in ""([^""]*)""")]
    public async Task WhenGoToDefinitionIsRequested(int line, int column, string fileName)
        => _ctx.LastDefinitions = await _ctx.Harness.Client
            .RequestDefinitionAsync(_ctx.UriFor(fileName), line, column)
            .ConfigureAwait(false);

    [Then(@"(\d+) definition location(?:s are|s is| is| are) returned")]
    public void ThenNDefinitionLocationsAreReturned(int expected)
    {
        var actual = _ctx.LastDefinitions?.Count() ?? 0;
        actual.Should().Be(expected, $"expected {expected} definition location(s) but got {actual}");
    }

    [Then(@"the definition locations include ""([^""]*)""")]
    public void ThenTheDefinitionLocationsInclude(string fileName)
    {
        _ctx.LastDefinitions.Should().NotBeNull("definition locations should have been returned");
        _ctx.LastDefinitions!.Should().Contain(
            l => l.Location!.Uri.ToString().EndsWith(fileName, StringComparison.OrdinalIgnoreCase),
            $"a definition location in '{fileName}' should be present");
    }

    [Then(@"the definition location is on line (\d+)")]
    public void ThenTheDefinitionLocationIsOnLine(int line)
    {
        _ctx.LastDefinitions.Should().NotBeNull("definition locations should have been returned");
        // Scenarios count lines the way an editor does (1-based); LSP ranges are 0-based.
        _ctx.LastDefinitions!.Single().Location!.Range.Start.Line.Should().Be(line - 1);
    }

    // ── F23 · textDocument/inlayHint ────────────────────────────────────────────

    [When(@"inlay hints are requested for ""([^""]*)"" from line (\d+) to line (\d+)")]
    public async Task WhenInlayHintsAreRequested(string fileName, int startLine, int endLine)
        => _ctx.LastInlayHints = await _ctx.Harness.Client
            // Scenarios name a 1-based inclusive line span. LSP's end position is exclusive, so
            // the 1-based end line number is already the right 0-based exclusive end.
            .RequestInlayHintsAsync(_ctx.UriFor(fileName), startLine - 1, endLine)
            .ConfigureAwait(false);

    [Then(@"(\d+) inlay hint(?:s are|s is| is| are) returned")]
    public void ThenNInlayHintsAreReturned(int expected)
    {
        var actual = _ctx.LastInlayHints?.Count() ?? 0;
        actual.Should().Be(expected, $"expected {expected} inlay hint(s) but got {actual}");
    }

    [Then(@"an inlay hint on line (\d+) has label ""([^""]*)""")]
    public void ThenAnInlayHintOnLineHasLabel(int line, string label)
    {
        _ctx.LastInlayHints.Should().NotBeNull("inlay hints should have been returned");
        var onLine = _ctx.LastInlayHints!.Where(h => h.Position.Line == line - 1).ToList();

        onLine.Should().NotBeEmpty($"an inlay hint should be anchored on line {line}, but hints were " +
            Describe(_ctx.LastInlayHints));
        onLine.Should().Contain(
            h => (h.Label.String ?? string.Empty).Contains(label, StringComparison.Ordinal),
            $"an inlay hint on line {line} should have a label containing '{label}', but they were " +
            Describe(_ctx.LastInlayHints));
    }

    [Then(@"no inlay hint is anchored on line (\d+)")]
    public void ThenNoInlayHintOnLine(int line)
    {
        _ctx.LastInlayHints.Should().NotBeNull("inlay hints should have been returned");
        _ctx.LastInlayHints!.Should().NotContain(
            h => h.Position.Line == line - 1,
            $"line {line} should carry no inlay hint, but the hints were " +
            Describe(_ctx.LastInlayHints));
    }

    // ── F26 · reqnroll/resolveTestTargets ───────────────────────────────────────

    [When(@"test targets are resolved for ""([^""]*)"" from line (\d+) to line (\d+)")]
    public async Task WhenTestTargetsAreResolved(string fileName, int startLine, int endLine)
        => _ctx.LastTestTargets = await _ctx.Harness.Client
            .RequestResolveTestTargetsAsync(_ctx.UriFor(fileName), startLine - 1, endLine)
            .ConfigureAwait(false);

    [Then(@"(\d+) test target(?:s are|s is| is| are) returned")]
    public void ThenNTestTargetsAreReturned(int expected)
    {
        var actual = _ctx.LastTestTargets?.Targets.Count ?? 0;
        actual.Should().Be(expected,
            $"expected {expected} test target(s) but got {actual}: " +
            string.Join("; ", _ctx.LastTestTargets?.Targets.Select(t => $"{t.DeclaringTypeFullName}.{t.MethodName}")
                              ?? Enumerable.Empty<string>()));
    }

    [Then(@"a test target has method ""([^""]*)""")]
    public void ThenATestTargetHasMethod(string methodName)
    {
        _ctx.LastTestTargets.Should().NotBeNull("test targets should have been returned");
        _ctx.LastTestTargets!.Targets.Should().Contain(
            t => t.MethodName == methodName,
            $"a target with method '{methodName}' should be present, but the targets were " +
            string.Join("; ", _ctx.LastTestTargets.Targets.Select(t => $"{t.DeclaringTypeFullName}.{t.MethodName}")));
    }

    [Then(@"a test target has method ""([^""]*)"" on type ""([^""]*)""")]
    public void ThenATestTargetHasMethodOnType(string methodName, string typeName)
    {
        _ctx.LastTestTargets.Should().NotBeNull("test targets should have been returned");
        _ctx.LastTestTargets!.Targets.Should().Contain(
            t => t.MethodName == methodName && t.DeclaringTypeFullName == typeName,
            $"a target '{typeName}.{methodName}' should be present, but the targets were " +
            string.Join("; ", _ctx.LastTestTargets.Targets.Select(t => $"{t.DeclaringTypeFullName}.{t.MethodName}")));
    }

    [Then(@"the test targets are parameterized")]
    public void ThenTheTestTargetsAreParameterized()
    {
        _ctx.LastTestTargets.Should().NotBeNull("test targets should have been returned");
        _ctx.LastTestTargets!.Targets.Should().OnlyContain(t => t.IsParameterized);
    }

    // ── F24 · reqnroll/goToMatchingScenarios ────────────────────────────────────

    [When(@"matching scenarios are requested at line (\d+) column (\d+) in ""([^""]*)""")]
    public async Task WhenMatchingScenariosAreRequested(int line, int column, string fileName)
        => _ctx.LastMatchingScenarios = await _ctx.Harness.Client
            .RequestGoToMatchingScenariosAsync(_ctx.UriFor(fileName), line, column)
            .ConfigureAwait(false);

    [Then(@"(\d+) matching scenario(?:s are|s is| is| are) returned")]
    public void ThenNMatchingScenariosAreReturned(int expected)
    {
        var actual = _ctx.LastMatchingScenarios?.Scenarios.Count ?? 0;
        actual.Should().Be(expected,
            $"expected {expected} matching scenario(s) but got {actual}: " +
            string.Join("; ", _ctx.LastMatchingScenarios?.Scenarios.Select(s => s.ScenarioName)
                              ?? Enumerable.Empty<string>()));
    }

    [Then(@"the matching scenarios include ""([^""]*)""")]
    public void ThenTheMatchingScenariosInclude(string scenarioName)
    {
        _ctx.LastMatchingScenarios.Should().NotBeNull("matching scenarios should have been returned");
        _ctx.LastMatchingScenarios!.Scenarios.Should().Contain(
            s => s.ScenarioName == scenarioName,
            $"'{scenarioName}' should be among the matching scenarios, but they were " +
            string.Join("; ", _ctx.LastMatchingScenarios.Scenarios.Select(s => s.ScenarioName)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string Describe(IEnumerable<OmniSharp.Extensions.LanguageServer.Protocol.Models.InlayHint>? hints)
        => hints is null || !hints.Any()
            ? "(none)"
            : string.Join("; ", hints.Select(h => $"line {h.Position.Line + 1}: {h.Label.String}"));
}
