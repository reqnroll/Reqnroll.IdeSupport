#nullable enable

using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class LspErrorTelemetryServiceTests
{
    private readonly ILspTelemetryService _lspTelemetryService = Substitute.For<ILspTelemetryService>();

    private LspErrorTelemetryService CreateSut() => new(_lspTelemetryService);

    [Fact]
    public void MonitorError_sends_an_UnhandledException_event_with_exception_type_and_message()
    {
        var sut = CreateSut();
        var exception = new InvalidOperationException("boom");

        sut.MonitorError(exception);

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props =>
                (string?)props["ExceptionType"] == typeof(InvalidOperationException).FullName &&
                (string?)props["Message"] == "boom" &&
                !props.ContainsKey("IsFatal")));
    }

    [Fact]
    public void MonitorError_attributes_a_thrown_exception_to_the_class_it_passed_through()
    {
        var thrown = CaptureThrown(() => throw new InvalidOperationException("boom"));

        CreateSut().MonitorError(thrown);

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props =>
                (string?)props["Source"] == nameof(LspErrorTelemetryServiceTests)));
    }

    [Fact]
    public async Task MonitorError_folds_a_compiler_generated_async_type_into_its_declaring_class()
    {
        var thrown = await CaptureThrownAsync();

        CreateSut().MonitorError(thrown);

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props =>
                (string?)props["Source"] == nameof(LspErrorTelemetryServiceTests)));
    }

    [Fact]
    public void MonitorError_omits_Source_for_an_exception_that_was_never_thrown()
    {
        CreateSut().MonitorError(new InvalidOperationException("never thrown"));

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props => !props.ContainsKey("Source")));
    }

    [Fact]
    public void MonitorError_attaches_sanitized_StackFrames_for_a_thrown_exception()
    {
        var thrown = CaptureThrown(() => ThrowWithSecret(@"C:\Users\alice\Login.feature"));

        CreateSut().MonitorError(thrown);

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props =>
                ((string?)props[TelemetryProperties.StackFrames])!.StartsWith(
                    "Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry.LspErrorTelemetryServiceTests.ThrowWithSecret") &&
                !((string?)props[TelemetryProperties.StackFrames])!.Contains("alice") &&
                !((string?)props[TelemetryProperties.StackFrames])!.Contains(".cs")));
    }

    [Fact]
    public void MonitorError_omits_StackFrames_for_an_exception_that_was_never_thrown()
    {
        CreateSut().MonitorError(new InvalidOperationException("never thrown"));

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props => !props.ContainsKey(TelemetryProperties.StackFrames)));
    }

    [Fact]
    public void MonitorError_sends_StackFrames_only_for_the_first_occurrence_of_a_stack_in_a_session()
    {
        var sut = CreateSut();

        for (var i = 0; i < 3; i++)
            sut.MonitorError(CaptureThrown(() => ThrowWithSecret("x")));

        // Every occurrence is still reported; only the first carries the stack.
        var calls = _lspTelemetryService.ReceivedCalls().ToList();
        calls.Should().HaveCount(3);
        calls.Count(c => ((Dictionary<string, object?>)c.GetArguments()[1]!).ContainsKey(TelemetryProperties.StackFrames))
            .Should().Be(1);
    }

    [Fact]
    public void MonitorError_caps_the_distinct_stacks_that_carry_StackFrames_per_session()
    {
        var sut = new LspErrorTelemetryService(_lspTelemetryService, maxDistinctStacks: 2);

        sut.MonitorError(CaptureThrown(() => ThrowWithSecret("x")));
        sut.MonitorError(CaptureThrown(() => throw new InvalidOperationException("other site")));
        sut.MonitorError(CaptureThrown(() => throw new ArgumentException("third site")));
        sut.MonitorError(CaptureThrown(() => throw new FormatException("fourth site")));

        var calls = _lspTelemetryService.ReceivedCalls().ToList();
        calls.Should().HaveCount(4);
        calls.Count(c => ((Dictionary<string, object?>)c.GetArguments()[1]!).ContainsKey(TelemetryProperties.StackFrames))
            .Should().Be(2);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowWithSecret(string secret) => throw new InvalidOperationException(secret);

    private static Exception CaptureThrown(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("action did not throw");
    }

    private static async Task<Exception> CaptureThrownAsync()
    {
        try
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        }
        catch (Exception ex) { return ex; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MonitorError_includes_IsFatal_when_specified(bool isFatal)
    {
        var sut = CreateSut();

        sut.MonitorError(new Exception("test"), isFatal);

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props => (bool)props["IsFatal"]! == isFatal));
    }

    [Fact]
    public void MonitorError_passes_the_message_through_unredacted_because_the_sink_scrubs_it()
    {
        var sut = CreateSut();
        const string message = @"Error reading C:\Users\alice\project\feature.feature";

        sut.MonitorError(new Exception(message));

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props => (string?)props["Message"] == message));
    }

    [Fact]
    public void Every_other_member_is_a_no_op()
    {
        var sut = CreateSut();

        sut.MonitorOpenProject(null!, null);
        sut.MonitorOpenFeatureFile(null!);
        sut.MonitorExtensionInstalled();
        sut.MonitorExtensionUpgraded("1.0.0");
        sut.MonitorExtensionDaysOfUsage(3);
        sut.MonitorCommandAddFeatureFile(null!);
        sut.MonitorCommandAddReqnrollConfigFile(null!);
        sut.MonitorProjectTemplateWizardStarted();
        sut.MonitorProjectTemplateWizardCompleted("net10.0", "xunit", false);
        sut.MonitorLinkClicked("source", "https://example.com");
        sut.MonitorUpgradeDialogDismissed(new Dictionary<string, object>());
        sut.MonitorWelcomeDialogDismissed(new Dictionary<string, object>());
        sut.TransmitEvent(null!);

        _lspTelemetryService.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
