using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Steps that assert on the <c>textDocument/publishDiagnostics</c> notifications the server pushes
/// for a feature file: Gherkin parse errors (design doc F3/F4, source <c>reqnroll.parser</c>) and
/// undefined / ambiguous step diagnostics (source <c>reqnroll.binding</c>).
/// <para>
/// Diagnostics arrive asynchronously and are republished whenever the match cache changes, so
/// every assertion polls for the expected end state rather than reading one push. LSP defines each
/// push as the complete set for its URI, so "no diagnostics" means the latest published set is
/// empty — which is a real assertion the server has to make true, distinct from never publishing.
/// </para>
/// </summary>
[Binding]
public sealed class DiagnosticsSteps
{
    private readonly LspScenarioContext _ctx;

    public DiagnosticsSteps(LspScenarioContext ctx) => _ctx = ctx;

    [Then(@"diagnostics are published for ""([^""]*)""")]
    public async Task ThenDiagnosticsArePublishedFor(string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        var arrived = await _ctx.Harness
            .WaitForDiagnosticsAsync(uri, p => p is not null)
            .ConfigureAwait(false);

        arrived.Should().BeTrue(
            $"the server should publish a textDocument/publishDiagnostics notification for '{fileName}'");
    }

    [Then(@"the published diagnostics for ""([^""]*)"" are empty")]
    public async Task ThenThePublishedDiagnosticsAreEmpty(string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        var empty = await _ctx.Harness
            .WaitForDiagnosticsAsync(uri, p => p is not null && p.Diagnostics.Count() == 0)
            .ConfigureAwait(false);

        empty.Should().BeTrue(
            $"'{fileName}' should end up with an empty diagnostic set, but the last publish was " +
            Describe(_ctx.Harness.PublishedDiagnosticsFor(uri)));
    }

    [Then(@"a ""([^""]*)"" diagnostic from ""([^""]*)"" is published for ""([^""]*)""")]
    public async Task ThenADiagnosticFromSourceIsPublished(string severity, string source, string fileName)
    {
        var expected = ParseSeverity(severity);
        var uri = _ctx.UriFor(fileName);

        var found = await _ctx.Harness.WaitForDiagnosticsAsync(uri, p =>
            p is not null && p.Diagnostics.Any(d =>
                d.Severity == expected &&
                string.Equals(d.Source, source, StringComparison.Ordinal))).ConfigureAwait(false);

        found.Should().BeTrue(
            $"'{fileName}' should carry a {severity} diagnostic from '{source}', but the last " +
            "publish was " + Describe(_ctx.Harness.PublishedDiagnosticsFor(uri)));
    }

    [Then(@"a ""([^""]*)"" diagnostic from ""([^""]*)"" is published for ""([^""]*)"" saying ""(.*)""")]
    public async Task ThenADiagnosticIsPublished(
        string severity, string source, string fileName, string message)
    {
        var expected = ParseSeverity(severity);
        var uri = _ctx.UriFor(fileName);

        var found = await _ctx.Harness.WaitForDiagnosticsAsync(uri, p =>
            p is not null && p.Diagnostics.Any(d =>
                d.Severity == expected &&
                string.Equals(d.Source, source, StringComparison.Ordinal) &&
                d.Message.Contains(message, StringComparison.Ordinal))).ConfigureAwait(false);

        found.Should().BeTrue(
            $"'{fileName}' should carry a {severity} diagnostic from '{source}' containing " +
            $"'{message}', but the last publish was " +
            Describe(_ctx.Harness.PublishedDiagnosticsFor(uri)));
    }

    [Then(@"a ""([^""]*)"" diagnostic from ""([^""]*)"" is published for ""([^""]*)"" on line (\d+)")]
    public async Task ThenADiagnosticIsPublishedOnLine(
        string severity, string source, string fileName, int line)
    {
        var expected = ParseSeverity(severity);
        var uri = _ctx.UriFor(fileName);

        // Scenarios count lines the way an editor does (1-based); LSP ranges are 0-based.
        var lspLine = line - 1;

        var found = await _ctx.Harness.WaitForDiagnosticsAsync(uri, p =>
            p is not null && p.Diagnostics.Any(d =>
                d.Severity == expected &&
                string.Equals(d.Source, source, StringComparison.Ordinal) &&
                d.Range.Start.Line == lspLine)).ConfigureAwait(false);

        found.Should().BeTrue(
            $"'{fileName}' should carry a {severity} diagnostic from '{source}' on line {line}, " +
            $"but the last publish was " + Describe(_ctx.Harness.PublishedDiagnosticsFor(uri)));
    }

    [Then(@"no ""([^""]*)"" diagnostic is published for ""([^""]*)""")]
    public async Task ThenNoDiagnosticFromSourceIsPublished(string source, string fileName)
    {
        var uri = _ctx.UriFor(fileName);

        // Wait for the first publish so this cannot pass merely because nothing has arrived yet,
        // then wait for the stream to go quiet. Absence must be asserted against the settled
        // state: a step is briefly reported undefined while a registry update propagates, and
        // asserting on the first set to arrive turns that flicker into a failure that reads
        // exactly like a lost binding.
        await _ctx.Harness.WaitForDiagnosticsAsync(uri, p => p is not null).ConfigureAwait(false);
        await _ctx.Harness.WaitForDiagnosticsQuiescenceAsync().ConfigureAwait(false);

        var published = _ctx.Harness.PublishedDiagnosticsFor(uri);
        published.Should().NotBeNull(
            $"the server should publish diagnostics for '{fileName}' at least once");
        published!.Diagnostics.Should().NotContain(
            d => d.Source == source,
            $"'{fileName}' should carry no '{source}' diagnostic, but the last publish was " +
            Describe(published));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static DiagnosticSeverity ParseSeverity(string severity)
        => Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"'{severity}' is not an LSP diagnostic severity " +
                $"({string.Join(", ", Enum.GetNames<DiagnosticSeverity>())}).", nameof(severity));

    private static string Describe(PublishDiagnosticsParams? published)
        => published is null
            ? "(nothing published for that URI)"
            : published.Diagnostics.Count() == 0
                ? "an empty set"
                : string.Join("; ", published.Diagnostics.Select(d =>
                    $"[{d.Severity} {d.Source} line {d.Range.Start.Line + 1}] {d.Message}"));
}
