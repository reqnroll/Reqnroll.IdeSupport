using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="CopyToClipboardInterceptor"/> copies the text of the server's
/// <c>reqnroll/copyToClipboard</c> and lets every message, including that one, pass through.
/// </summary>
public class CopyToClipboardInterceptorTests
{
    private const string Snippet = "    [When(\"I press add\")]\r\n    public void WhenIPressAdd()\r\n    {\r\n    }\r\n";

    private readonly List<string> _copied = new();

    private CopyToClipboardInterceptor CreateSut() => new(text => _copied.Add(text));

    private static LspMessage Message(LspMessageDirection direction, string method, JToken? @params) =>
        new(direction, new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        }, DateTimeOffset.Now);

    [Fact]
    public async Task Copies_the_text_of_a_copy_notification_and_passes_it_through()
    {
        var result = await CreateSut().InterceptAsync(
            Message(LspMessageDirection.Receive, "reqnroll/copyToClipboard", new JObject { ["text"] = Snippet }),
            CancellationToken.None);

        _copied.Should().Equal(Snippet);
        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    public static IEnumerable<object?[]> UnusableParams() => new[]
    {
        new object?[] { null },
        new object?[] { new JObject() },
        new object?[] { new JObject { ["text"] = "" } },
        new object?[] { new JObject { ["text"] = 42 } },
        new object?[] { new JArray(Snippet) },
    };

    [Theory]
    [MemberData(nameof(UnusableParams))]
    public async Task Copies_nothing_when_the_notification_has_no_usable_text(JToken? @params)
    {
        var result = await CreateSut().InterceptAsync(
            Message(LspMessageDirection.Receive, "reqnroll/copyToClipboard", @params),
            CancellationToken.None);

        _copied.Should().BeEmpty();
        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task Ignores_other_methods_and_outgoing_messages()
    {
        var sut = CreateSut();
        var @params = new JObject { ["text"] = Snippet };

        await sut.InterceptAsync(Message(LspMessageDirection.Receive, "reqnroll/logMessage", @params), CancellationToken.None);
        await sut.InterceptAsync(Message(LspMessageDirection.Send, "reqnroll/copyToClipboard", @params), CancellationToken.None);

        _copied.Should().BeEmpty();
    }
}
