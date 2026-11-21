using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class PublishedDiagnosticsCacheTests
{
    private static readonly DocumentUri Uri1 = DocumentUri.FromFileSystemPath("/w/a.feature");
    private static readonly DocumentUri Uri2 = DocumentUri.FromFileSystemPath("/w/b.feature");

    private readonly PublishedDiagnosticsCache _sut = new();

    private static Diagnostic D(string message = "m") => new()
    {
        Range = new LspRange(1, 2, 1, 5),
        Severity = DiagnosticSeverity.Warning,
        Source = "s",
        Message = message
    };

    private bool Try(DocumentUri uri, params Diagnostic[] set) => _sut.TryPublish(uri, set, () => true, () => { });

    [Fact]
    public void Identical_set_is_skipped_and_changed_set_is_published()
    {
        Try(Uri1, D()).Should().BeTrue();
        Try(Uri1, D()).Should().BeFalse();
        Try(Uri1, D("other")).Should().BeTrue();
    }

    [Fact]
    public void Order_of_the_set_does_not_matter_but_duplicates_do()
    {
        Try(Uri1, D("a"), D("b")).Should().BeTrue();
        Try(Uri1, D("b"), D("a")).Should().BeFalse();
        Try(Uri1, D("b"), D("a"), D("a")).Should().BeTrue();
    }

    [Fact]
    public void Every_displayed_field_is_part_of_the_signature()
    {
        var variants = new Func<Diagnostic, Diagnostic>[]
        {
            d => d with { Severity = DiagnosticSeverity.Error },
            d => d with { Code = new DiagnosticCode("X1") },
            d => d with { Code = new DiagnosticCode(7) },
            d => d with { Source = "t" },
            d => d with { Message = "n" },
            d => d with { Range = new LspRange(1, 2, 1, 6) },
            d => d with { Tags = new Container<DiagnosticTag>(DiagnosticTag.Unnecessary) },
            d => d with { CodeDescription = new CodeDescription { Href = new Uri("https://example.test/x") } },
            d => d with { Data = JToken.Parse("{\"a\":1}") },
            d => d with { RelatedInformation = new Container<DiagnosticRelatedInformation>(new DiagnosticRelatedInformation
                { Location = new Location { Uri = Uri2, Range = new LspRange(0, 0, 0, 1) }, Message = "r" }) },
        };

        foreach (var make in variants)
        {
            var sut = new PublishedDiagnosticsCache();
            sut.TryPublish(Uri1, new[] { D() }, () => true, () => { }).Should().BeTrue();
            sut.TryPublish(Uri1, new[] { make(D()) }, () => true, () => { }).Should().BeTrue();
        }
    }

    [Fact]
    public void Empty_set_is_published_first_and_when_replacing_non_empty_then_skipped_when_repeated()
    {
        Try(Uri1).Should().BeTrue();
        Try(Uri1).Should().BeFalse();
        Try(Uri1, D()).Should().BeTrue();
        Try(Uri1).Should().BeTrue();
        Try(Uri1).Should().BeFalse();
    }

    [Fact]
    public void Reset_Evict_and_Clear_force_the_next_publish()
    {
        Try(Uri1, D());
        _sut.Reset(Uri1);
        Try(Uri1, D()).Should().BeTrue();

        _sut.Evict(Uri1);
        Try(Uri1, D()).Should().BeTrue();

        Try(Uri2, D());
        _sut.Clear();
        Try(Uri1, D()).Should().BeTrue();
        Try(Uri2, D()).Should().BeTrue();
    }

    [Fact]
    public void Uris_are_independent()
    {
        Try(Uri1, D()).Should().BeTrue();
        Try(Uri2, D()).Should().BeTrue();
        Try(Uri1, D()).Should().BeFalse();
        _sut.Evict(Uri1);
        Try(Uri2, D()).Should().BeFalse();
    }

    [Fact]
    public void Publish_for_a_closed_document_sends_nothing_and_leaves_no_entry()
    {
        var sent = false;
        _sut.TryPublish(Uri1, new[] { D() }, () => false, () => sent = true).Should().BeFalse();
        sent.Should().BeFalse();

        // Had an entry been created, this identical set would be skipped.
        Try(Uri1, D()).Should().BeTrue();
    }

    [Fact]
    public void Concurrent_identical_publishes_for_one_uri_send_exactly_once()
    {
        var sends = 0;
        Parallel.For(0, 200, _ =>
            _sut.TryPublish(Uri1, new[] { D() }, () => true, () => Interlocked.Increment(ref sends)));

        sends.Should().Be(1);
    }

    [Fact]
    public void Concurrent_publishes_leave_the_last_sent_set_as_the_recorded_one()
    {
        var lastSent = "";
        var gate = new object();
        Parallel.For(0, 200, i =>
        {
            var message = "m" + (i % 4);
            _sut.TryPublish(Uri1, new[] { D(message) }, () => true, () => { lock (gate) lastSent = message; });
        });

        // The recorded signature must match what the client received last: re-publishing it is skipped.
        Try(Uri1, D(lastSent)).Should().BeFalse();
    }

    [Fact]
    public async Task Publish_racing_close_never_resurrects_the_entry()
    {
        for (var round = 0; round < 200; round++)
        {
            var sut = new PublishedDiagnosticsCache();
            var open = true;
            using var start = new ManualResetEventSlim();

            var publisher = Task.Run(() =>
            {
                start.Wait();
                sut.TryPublish(Uri1, new[] { D() }, () => Volatile.Read(ref open), () => { });
            });
            var closer = Task.Run(() =>
            {
                start.Wait();
                Volatile.Write(ref open, false); // the buffer removal
                sut.Evict(Uri1);
            });
            start.Set();
            await Task.WhenAll(publisher, closer);

            // Reopened with a fresh session: the first publish must go out, which fails if an entry survived the close.
            sut.TryPublish(Uri1, new[] { D() }, () => true, () => { }).Should().BeTrue();
        }
    }
}
