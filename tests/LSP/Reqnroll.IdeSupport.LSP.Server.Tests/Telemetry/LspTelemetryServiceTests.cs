#nullable enable

using System.Reflection;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class LspTelemetryServiceTests
{
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();

    private LspTelemetryService CreateSut() => new(_languageServer);

    [Fact]
    public void SendEvent_calls_SendNotification_with_telemetry_event_method()
    {
        var sut = CreateSut();

        sut.SendEvent("TestEvent", new Dictionary<string, object?>());

        _languageServer.Received(1).SendNotification("telemetry/event", Arg.Any<object>());
    }

    [Fact]
    public void SendEvent_includes_eventName_and_properties_in_params()
    {
        var sut = CreateSut();
        var properties = new Dictionary<string, object?>
        {
            ["Key1"] = "value1",
            ["Count"] = 42,
        };

        sut.SendEvent("MyEvent", properties);

        _languageServer.Received(1).SendNotification(
            "telemetry/event",
            Arg.Is<object>(o => HasEventName(o, "MyEvent") && HasProperty(o, "Key1", "value1") && HasProperty(o, "Count", 42)));
    }

    [Theory]
    [InlineData("ErrorMessage", @"Could not load C:\Users\someone\proj\bin\x.dll", "Could not load <path>")]
    [InlineData("Message", "Could not load /home/someone/proj/x.dll", "Could not load <path>")]
    public void SendEvent_redacts_filesystem_paths_from_free_text_properties_without_mutating_input(
        string key, string raw, string expected)
    {
        var sut = CreateSut();
        var properties = new Dictionary<string, object?> { [key] = raw, ["Protocol"] = "textDocument/definition" };

        sut.SendEvent("MyEvent", properties);

        _languageServer.Received(1).SendNotification(
            "telemetry/event",
            Arg.Is<object>(o => HasProperty(o, key, expected) && HasProperty(o, "Protocol", "textDocument/definition")));
        properties[key].Should().Be(raw, "callers (and the debug-log mirror) must keep the raw text");
    }

    [Fact]
    public void Debug_log_mirror_keeps_raw_text_while_the_wire_copy_is_scrubbed()
    {
        const string raw = @"Could not load C:\Users\someone\x.dll";
        var debugLog = Substitute.For<Reqnroll.IdeSupport.Common.Logging.ITelemetryDebugLog>();
        var sut = new FileLoggingLspTelemetryService(CreateSut(), debugLog);

        sut.SendEvent("MyEvent", new Dictionary<string, object?> { ["ErrorMessage"] = raw });

        debugLog.Received(1).Record("server", "MyEvent",
            Arg.Is<object?>(d => raw.Equals(((Dictionary<string, object?>)d!)["ErrorMessage"])),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string?>());
        _languageServer.Received(1).SendNotification(
            "telemetry/event", Arg.Is<object>(o => HasProperty(o, "ErrorMessage", "Could not load <path>")));
    }

    private static bool HasEventName(object obj, string expectedName)
    {
        var eventName = obj.GetType().GetProperty("eventName")?.GetValue(obj) as string;
        return eventName == expectedName;
    }

    private static bool HasProperty(object obj, string key, object expectedValue)
    {
        var props = obj.GetType().GetProperty("properties")?.GetValue(obj);
        if (props is not Dictionary<string, object?> dict)
            return false;
        return dict.TryGetValue(key, out var val) && val?.Equals(expectedValue) == true;
    }
}
