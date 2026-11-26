using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks -- no JoinableTaskFactory/UI thread in these tests

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="SourceControlEditCheckInterceptor"/> holds back a server-pushed
/// <c>workspace/applyEdit</c> that changes read-only files, asks source control, and then either
/// forwards it to Visual Studio or answers the server <c>applied: false</c>.
/// </summary>
public class SourceControlEditCheckInterceptorTests
{
    private const string FeaturePath = @"C:\work\Specs\Calc.feature";
    private const string StepsPath   = @"C:\work\Specs\StepDefinitions\CalcSteps.cs";

    private readonly HashSet<string> _readOnly = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IReadOnlyList<string>> _queried = new();
    private readonly List<JObject> _forwarded = new();
    private readonly List<(JToken Id, JObject Result)> _responses = new();
    private (bool Allowed, uint MoreInfo) _verdict = (true, 0);
    private Exception? _queryFailure;
    private bool _forwardSucceeds = true;

    private SourceControlEditCheckInterceptor CreateSut() => new(
        NullLogger<SourceControlEditCheckInterceptor>.Instance,
        (files, _) =>
        {
            _queried.Add(files);
            return _queryFailure is null ? Task.FromResult(_verdict) : Task.FromException<(bool, uint)>(_queryFailure);
        },
        (body, _) => { _forwarded.Add(body); return Task.FromResult(_forwardSucceeds); },
        (id, result, _) => { _responses.Add((id, result)); return Task.FromResult(true); },
        path => _readOnly.Contains(path));

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    private static JObject ApplyEdit(int id, params string[] paths) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "workspace/applyEdit",
        ["params"] = new JObject
        {
            ["edit"] = new JObject
            {
                ["documentChanges"] = new JArray(paths.Select(p => new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = FileUri(p), ["version"] = null },
                    ["edits"] = new JArray(),
                })),
            },
        },
    };

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);

    [Fact]
    public async Task Passes_an_edit_of_writable_files_straight_through_without_asking()
    {
        var sut = CreateSut();

        var result = await sut.InterceptAsync(Receive(ApplyEdit(1, FeaturePath, StepsPath)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        _queried.Should().BeEmpty();
    }

    [Fact]
    public async Task Holds_back_an_edit_of_a_read_only_file_and_forwards_it_once_editing_is_allowed()
    {
        _readOnly.Add(StepsPath);
        var sut = CreateSut();
        var request = ApplyEdit(1, FeaturePath, StepsPath);

        var result = await sut.InterceptAsync(Receive(request), CancellationToken.None);
        await sut.LastCheck;

        result.Should().Be(LspInterceptorResult.Consume);
        _queried.Should().ContainSingle().Which.Should().Equal(StepsPath);
        _forwarded.Should().ContainSingle().Which.Should().BeSameAs(request);
        _responses.Should().BeEmpty();
    }

    [Fact]
    public async Task Answers_applied_false_and_does_not_forward_when_editing_is_refused()
    {
        _readOnly.Add(StepsPath);
        _verdict = (false, 32);
        var sut = CreateSut();

        await sut.InterceptAsync(Receive(ApplyEdit(42, FeaturePath, StepsPath)), CancellationToken.None);
        await sut.LastCheck;

        _forwarded.Should().BeEmpty("no file may change when editing one of them is refused");
        var (id, response) = _responses.Should().ContainSingle().Subject;
        id.Value<int>().Should().Be(42);
        response["applied"]!.Value<bool>().Should().BeFalse();
        response["failureReason"]!.Value<string>().Should().Be("CalcSteps.cs is read-only and was not made writable");
    }

    [Fact]
    public async Task Asks_about_every_read_only_file_in_one_question()
    {
        _readOnly.Add(FeaturePath);
        _readOnly.Add(StepsPath);
        var sut = CreateSut();

        await sut.InterceptAsync(Receive(ApplyEdit(1, FeaturePath, StepsPath)), CancellationToken.None);
        await sut.LastCheck;

        _queried.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { FeaturePath, StepsPath });
    }

    [Fact]
    public async Task Forwards_the_edit_when_asking_source_control_fails()
    {
        _readOnly.Add(StepsPath);
        _queryFailure = new InvalidOperationException("no shell");
        var sut = CreateSut();

        await sut.InterceptAsync(Receive(ApplyEdit(1, StepsPath)), CancellationToken.None);
        await sut.LastCheck;

        _forwarded.Should().ContainSingle();
        _responses.Should().BeEmpty();
    }

    [Fact]
    public async Task Answers_applied_false_when_the_held_back_edit_cannot_be_forwarded()
    {
        _readOnly.Add(StepsPath);
        _forwardSucceeds = false;
        var sut = CreateSut();

        await sut.InterceptAsync(Receive(ApplyEdit(5, StepsPath)), CancellationToken.None);
        await sut.LastCheck;

        _responses.Should().ContainSingle()
            .Which.Result["applied"]!.Value<bool>().Should().BeFalse("the server must not wait forever for an answer");
    }

    [Fact]
    public async Task Does_not_wait_for_the_source_control_question()
    {
        _readOnly.Add(StepsPath);
        var answered = new TaskCompletionSource<(bool, uint)>();
        var sut = new SourceControlEditCheckInterceptor(
            NullLogger<SourceControlEditCheckInterceptor>.Instance,
            (_, _) => answered.Task,
            (_, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult(true),
            path => _readOnly.Contains(path));

        var intercept = sut.InterceptAsync(Receive(ApplyEdit(1, StepsPath)), CancellationToken.None);

        intercept.IsCompleted.Should().BeTrue("a prompt must not hold up the server's other messages");
        answered.SetResult((true, 0));
        await sut.LastCheck;
    }

    [Fact]
    public async Task Reads_the_files_from_the_changes_map_too()
    {
        _readOnly.Add(StepsPath);
        var sut = CreateSut();
        var request = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "workspace/applyEdit",
            ["params"] = new JObject
            {
                ["edit"] = new JObject { ["changes"] = new JObject { [FileUri(StepsPath)] = new JArray() } },
            },
        };

        var result = await sut.InterceptAsync(Receive(request), CancellationToken.None);
        await sut.LastCheck;

        result.Should().Be(LspInterceptorResult.Consume);
        _queried.Should().ContainSingle().Which.Should().Equal(StepsPath);
    }

    [Fact]
    public async Task Ignores_other_methods_notifications_and_outgoing_messages()
    {
        _readOnly.Add(StepsPath);
        var sut = CreateSut();
        var notification = ApplyEdit(1, StepsPath);
        notification.Remove("id");
        var other = ApplyEdit(2, StepsPath);
        other["method"] = "workspace/configuration";

        (await sut.InterceptAsync(Receive(notification), CancellationToken.None)).Should().Be(LspInterceptorResult.PassThrough);
        (await sut.InterceptAsync(Receive(other), CancellationToken.None)).Should().Be(LspInterceptorResult.PassThrough);
        (await sut.InterceptAsync(new LspMessage(LspMessageDirection.Send, ApplyEdit(3, StepsPath), DateTimeOffset.Now), CancellationToken.None))
            .Should().Be(LspInterceptorResult.PassThrough);
        _queried.Should().BeEmpty();
    }

    [Fact]
    public void Describes_several_refused_files_by_name()
    {
        SourceControlEditCheckInterceptor.DescribeRefusal(new[] { @"C:\a\One.cs", @"C:\a\Two.feature" })
            .Should().Be("One.cs, Two.feature are read-only and were not made writable");
        SourceControlEditCheckInterceptor.DescribeRefusal(new[] { "1.cs", "2.cs", "3.cs", "4.cs", "5.cs" })
            .Should().Be("1.cs, 2.cs, 3.cs and 2 more are read-only and were not made writable");
    }
}
