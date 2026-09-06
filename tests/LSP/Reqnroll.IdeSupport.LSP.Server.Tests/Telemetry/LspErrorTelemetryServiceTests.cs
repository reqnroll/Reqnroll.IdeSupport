#nullable enable

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

    [Theory]
    [InlineData(@"Error reading C:\Users\alice\project\feature.feature", @"Error reading <path>")]
    [InlineData(@"Failed at /home/bob/project/feature.feature", @"Failed at <path>")]
    [InlineData(@"UNC failure \\server\share\file.feature", @"UNC failure <path>")]
    [InlineData("No paths here", "No paths here")]
    [InlineData("", "")]
    public void MonitorError_redacts_filesystem_paths_from_the_message(string message, string expected)
    {
        var sut = CreateSut();

        sut.MonitorError(new Exception(message));

        _lspTelemetryService.Received(1).SendEvent(
            TelemetryEvents.UnhandledException,
            Arg.Is<Dictionary<string, object?>>(props => (string?)props["Message"] == expected));
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
