using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.Classification;

public class SemanticTokensClassificationInterceptorTests
{
    private static readonly string[] Legend =
    {
        "reqnroll.keyword", "reqnroll.tag", "reqnroll.description", "reqnroll.comment",
        "reqnroll.doc_string", "reqnroll.data_table", "reqnroll.data_table_header",
        "reqnroll.step_parameter", "reqnroll.scenario_outline_placeholder", "reqnroll.undefined_step",
    };

    private static (SemanticTokenClassificationStore store, SemanticTokensClassificationInterceptor sut) Create()
    {
        var store = new SemanticTokenClassificationStore();
        return (store, new SemanticTokensClassificationInterceptor(store, NullLogger<SemanticTokensClassificationInterceptor>.Instance));
    }

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);

    /// <summary>Records whether anything was logged at Warning level or above, without depending on the ILogger extension-method shape.</summary>
    private sealed class WarningTrackingLogger : ILogger<SemanticTokensClassificationInterceptor>
    {
        public bool WarningLogged { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) WarningLogged = true;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static LspMessage InitializeResponse(string[] legend) => Receive(new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 2,
        ["result"] = new JObject
        {
            ["capabilities"] = new JObject
            {
                ["semanticTokensProvider"] = new JObject
                {
                    ["legend"] = new JObject
                    {
                        ["tokenTypes"] = new JArray(legend),
                        ["tokenModifiers"] = new JArray(),
                    },
                },
            },
        },
    });

    private static LspMessage SemanticTokensRequest(string uri, int id) => new(LspMessageDirection.Send, new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "textDocument/semanticTokens/full",
        ["params"] = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    }, DateTimeOffset.Now);

    private static LspMessage PushNotification(string uri, int[] data) => Receive(new JObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "reqnroll/semanticTokens",
        ["params"] = new JObject
        {
            ["uri"] = uri,
            ["version"] = 1,
            ["data"] = JArray.FromObject(data),
        },
    });

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_notification_decodes_tokens_into_the_store_using_the_legend()
    {
        const string uri = "file:///c:/w/A.feature";
        var (store, sut) = Create();

        await sut.InterceptAsync(InitializeResponse(Legend), CancellationToken.None);
        // Two tokens: (line0,char0,len8 → reqnroll.keyword) and (line2,char0,len5 → reqnroll.comment).
        await sut.InterceptAsync(PushNotification(uri, new[] { 0, 0, 8, 0, 0, 2, 0, 5, 3, 0 }), CancellationToken.None);

        var key = SemanticTokenClassificationStore.NormalizeKey(uri)!;
        store.TryGetTokens(key, out var tokens).Should().BeTrue();
        tokens.Should().HaveCount(2);

        tokens[0].Line.Should().Be(0);
        tokens[0].StartChar.Should().Be(0);
        tokens[0].Length.Should().Be(8);
        tokens[0].TokenType.Should().Be("reqnroll.keyword");

        tokens[1].Line.Should().Be(2);          // delta-line 2
        tokens[1].StartChar.Should().Be(0);
        tokens[1].Length.Should().Be(5);
        tokens[1].TokenType.Should().Be("reqnroll.comment"); // legend index 3
    }

    [Fact]
    public async Task Push_notification_is_passed_through_untouched()
    {
        var (_, sut) = Create();
        await sut.InterceptAsync(InitializeResponse(Legend), CancellationToken.None);

        var result = await sut.InterceptAsync(
            PushNotification("file:///c:/w/A.feature", new[] { 0, 0, 8, 0, 0 }), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough, "the interceptor only observes; VS still ignores the notification");
    }

    [Fact]
    public async Task Unrelated_message_passes_through_and_stores_nothing()
    {
        var (store, sut) = Create();

        var result = await sut.InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        store.TryGetTokens(SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Response_with_array_result_does_not_throw_or_log_a_warning()
    {
        var store = new SemanticTokenClassificationStore();
        var logger = new WarningTrackingLogger();
        var sut = new SemanticTokensClassificationInterceptor(store, logger);

        // e.g. a textDocument/documentSymbol response: "result" is a JArray, not a JObject.
        var response = Receive(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 5,
            ["result"] = new JArray(new JObject { ["name"] = "Scenario" }),
        });

        var result = await sut.InterceptAsync(response, CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        logger.WarningLogged.Should().BeFalse("a non-JObject result is a routine case, not a failure");
    }

    [Fact]
    public async Task Response_with_scalar_result_does_not_throw_or_log_a_warning()
    {
        var store = new SemanticTokenClassificationStore();
        var logger = new WarningTrackingLogger();
        var sut = new SemanticTokensClassificationInterceptor(store, logger);

        // e.g. a request whose "result" is a bare boolean/string.
        var response = Receive(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 6,
            ["result"] = true,
        });

        var result = await sut.InterceptAsync(response, CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        logger.WarningLogged.Should().BeFalse("a non-JObject result is a routine case, not a failure");
    }

    [Fact]
    public async Task Matched_response_with_array_result_does_not_throw_or_log_a_warning()
    {
        var store = new SemanticTokenClassificationStore();
        var logger = new WarningTrackingLogger();
        var sut = new SemanticTokensClassificationInterceptor(store, logger);

        await sut.InterceptAsync(SemanticTokensRequest("file:///c:/w/A.feature", 7), CancellationToken.None);

        // A malformed/unexpected server reply whose "result" is a JArray rather than an object.
        var response = Receive(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 7,
            ["result"] = new JArray(1, 2, 3),
        });

        var result = await sut.InterceptAsync(response, CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        logger.WarningLogged.Should().BeFalse("a non-JObject result is a routine case, not a failure");
        store.TryGetTokens(SemanticTokenClassificationStore.NormalizeKey("file:///c:/w/A.feature")!, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Matched_response_with_scalar_result_does_not_throw_or_log_a_warning()
    {
        var store = new SemanticTokenClassificationStore();
        var logger = new WarningTrackingLogger();
        var sut = new SemanticTokensClassificationInterceptor(store, logger);

        await sut.InterceptAsync(SemanticTokensRequest("file:///c:/w/A.feature", 8), CancellationToken.None);

        // A malformed/unexpected server reply whose "result" is a bare boolean.
        var response = Receive(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 8,
            ["result"] = true,
        });

        var result = await sut.InterceptAsync(response, CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        logger.WarningLogged.Should().BeFalse("a non-JObject result is a routine case, not a failure");
        store.TryGetTokens(SemanticTokenClassificationStore.NormalizeKey("file:///c:/w/A.feature")!, out _).Should().BeFalse();
    }
}
