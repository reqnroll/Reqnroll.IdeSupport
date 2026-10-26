using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.HookFeatureCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// The document URI string a CodeLens provider or command hands to its service must reach the server
/// unchanged, whatever characters the path contains. Each service builds its params by hand, so a
/// space, <c>#</c>, <c>%</c>, a quote or non-ASCII text in the path must survive the JSON encoding.
/// </summary>
public class DocumentUriRoundTripTests
{
    public static TheoryData<string> Uris => new()
    {
        "file:///c:/w/A%20folder%20with%20spaces/A%20Feature%20File.feature",
        "file:///c:/w/A folder with spaces/A Feature File.feature",
        "file:///c:/w/100%25/Hash%23Name.feature",
        "file:///c:/w/%C3%BCber/f%C3%A4ture.feature",
        "file:///c:/w/ünïcödé/fëature.feature",
        "file:///c:/w/it's \"quoted\"/back\\slash.feature",
    };

    private static async Task<string> SentUriAsync(Func<ScriptedServerPipe, Task> call)
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("null");
        try { await call(server); }
        catch (Exception) { /* a null result may be rejected; only the request sent matters */ }

        var request = server.Requests.Should().ContainSingle().Subject;
        return request["params"]!["textDocument"]!["uri"]!.Value<string>()!;
    }

    [Theory]
    [MemberData(nameof(Uris))]
    public async Task Step_code_lens_request_carries_the_uri_unchanged(string uri) =>
        (await SentUriAsync(s => new StepCodeLensService(s.Pipe, NullLogger<StepCodeLensService>.Instance)
            .GetLensesAsync(uri, CancellationToken.None))).Should().Be(uri);

    [Theory]
    [MemberData(nameof(Uris))]
    public async Task Hook_code_lens_request_carries_the_uri_unchanged(string uri) =>
        (await SentUriAsync(s => new HookFeatureCodeLensService(s.Pipe, NullLogger<HookFeatureCodeLensService>.Instance)
            .GetLensesAsync(uri, CancellationToken.None))).Should().Be(uri);

    [Theory]
    [MemberData(nameof(Uris))]
    public async Task Rename_targets_request_carries_the_uri_unchanged(string uri) =>
        (await SentUriAsync(s => new RenameStepService(s.Pipe, NullLogger<RenameStepService>.Instance)
            .GetRenameTargetsAsync(uri, 1, 2, CancellationToken.None))).Should().Be(uri);

    [Theory]
    [MemberData(nameof(Uris))]
    public async Task Find_hooks_request_carries_the_uri_unchanged(string uri) =>
        (await SentUriAsync(s => new FindHooksService(s.Pipe, NullLogger<FindHooksService>.Instance)
            .FindHooksAsync(uri, 1, 2, CancellationToken.None))).Should().Be(uri);

    [Theory]
    [MemberData(nameof(Uris))]
    public async Task Find_step_usages_request_carries_the_uri_unchanged(string uri) =>
        (await SentUriAsync(s => new FindStepUsagesService(s.Pipe, NullLogger<FindStepUsagesService>.Instance)
            .FindUsagesAsync(uri, 1, 2, CancellationToken.None))).Should().Be(uri);

    [Fact]
    public void Absolute_uri_keeps_a_space_escaped_where_ToString_does_not()
    {
        // Documents why the call sites that use Uri.ToString() and the document-activated
        // notification (AbsoluteUri) can differ for the same file.
        var uri = new Uri(@"c:\w\A folder with spaces\A Feature.feature");

        uri.AbsoluteUri.Should().Contain("A%20folder%20with%20spaces");
        uri.ToString().Should().Contain("A folder with spaces");
    }
}
