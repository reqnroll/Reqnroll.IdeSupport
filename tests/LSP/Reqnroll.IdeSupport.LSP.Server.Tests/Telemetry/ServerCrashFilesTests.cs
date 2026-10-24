using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public sealed class ServerCrashFilesTests : IDisposable
{
    private const string SessionId = "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00";
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reqnroll-crash-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ServerCrashFiles _sut;

    public ServerCrashFilesTests()
    {
        _sut = new ServerCrashFiles(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static Exception Thrown(Func<Exception> create)
    {
        try
        {
            throw create();
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static ServerCrashRecord Record(DateTime crashedAtUtc, string sessionId = SessionId) =>
        ServerCrashRecord.From(
            Thrown(() => new InvalidOperationException("boom")), crashedAtUtc, 1234, sessionId,
            "11111111-2222-3333-4444-555555555555", "vscode", "2026.1.1");

    private List<ServerCrashRecord> ReportPending(DateTime now)
    {
        var sent = new List<ServerCrashRecord>();
        _sut.ReportPending(sent.Add, now);
        return sent;
    }

    [Fact]
    public void A_written_record_is_reported_once_by_the_next_start_and_then_deleted()
    {
        _sut.TryWrite(Record(Now.AddMinutes(-5))).Should().BeTrue();

        var first = ReportPending(Now);
        var second = ReportPending(Now);

        first.Should().ContainSingle().Which.SessionId.Should().Be(SessionId);
        second.Should().BeEmpty();
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public void A_record_older_than_72_hours_is_deleted_unsent()
    {
        _sut.TryWrite(Record(Now.AddHours(-73))).Should().BeTrue();
        File.SetLastWriteTimeUtc(Directory.GetFiles(_directory).Single(), Now.AddHours(-73));

        ReportPending(Now).Should().BeEmpty();
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public void A_record_just_inside_72_hours_is_reported()
    {
        _sut.TryWrite(Record(Now.AddHours(-71))).Should().BeTrue();
        File.SetLastWriteTimeUtc(Directory.GetFiles(_directory).Single(), Now.AddHours(-71));

        ReportPending(Now).Should().ContainSingle();
    }

    [Fact]
    public void One_start_reports_at_most_the_cap_and_leaves_the_rest_for_the_next()
    {
        for (var i = 0; i < ServerCrashFiles.MaxReportsPerStart + 3; i++)
            _sut.TryWrite(Record(Now.AddMinutes(-1), ServerSession.NewId()));

        ReportPending(Now).Should().HaveCount(ServerCrashFiles.MaxReportsPerStart);
        ReportPending(Now).Should().HaveCount(3);
    }

    [Fact]
    public void An_unreadable_record_is_deleted_unsent()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "reqnroll-crash-" + SessionId + ".json"), "{ not json");

        ReportPending(Now).Should().BeEmpty();
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public void A_record_of_an_unknown_schema_version_is_not_sent()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "reqnroll-crash-" + SessionId + ".json"), "{\"SchemaVersion\":99}");

        ReportPending(Now).Should().BeEmpty();
    }

    [Fact]
    public void A_file_another_server_is_reporting_is_left_alone_until_abandoned()
    {
        Directory.CreateDirectory(_directory);
        var claimed = Path.Combine(_directory, "reqnroll-crash-" + SessionId + ".json.reporting-42");
        File.WriteAllText(claimed, "{}");
        File.SetLastWriteTimeUtc(claimed, Now.AddMinutes(-1));

        ReportPending(Now).Should().BeEmpty();
        File.Exists(claimed).Should().BeTrue();

        File.SetLastWriteTimeUtc(claimed, Now.AddMinutes(-11));
        ReportPending(Now);
        File.Exists(claimed).Should().BeFalse();
    }

    [Fact]
    public void A_missing_directory_reports_nothing()
    {
        ReportPending(Now).Should().BeEmpty();
    }

    [Fact]
    public void A_throwing_sender_does_not_stop_the_other_records()
    {
        _sut.TryWrite(Record(Now.AddMinutes(-1), ServerSession.NewId()));
        _sut.TryWrite(Record(Now.AddMinutes(-1), ServerSession.NewId()));
        var calls = 0;

        var act = () => _sut.ReportPending(_ => { calls++; throw new InvalidOperationException("send failed"); }, Now);

        act.Should().NotThrow();
        calls.Should().Be(2);
    }
}

public class ServerCrashRecordTests
{
    private static readonly DateTime CrashedAt = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static Exception Nested()
    {
        try
        {
            try
            {
                try
                {
                    throw new IOException("Could not read 'C:\\Users\\me\\secret.feature'");
                }
                catch (Exception ex)
                {
                    throw new ApplicationException("middle", ex);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("outer for \"MyStep\"", ex);
            }
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Records_the_outer_and_innermost_exception_already_sanitized()
    {
        var record = ServerCrashRecord.From(Nested(), CrashedAt, 500, "s", "c", "rider", "1.0");

        record.ExceptionType.Should().Be("System.InvalidOperationException");
        record.Message.Should().Be("outer for <text>");
        record.StackFrames.Should().StartWith("Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry.ServerCrashRecordTests.Nested");
        record.Source.Should().Be(nameof(ServerCrashRecordTests));
        record.InnerExceptionCount.Should().Be(2);
        record.InnerExceptionType.Should().Be("System.IO.IOException");
        record.InnerMessage.Should().Be("Could not read <text>");
        record.InnerStackFrames.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Event_properties_carry_the_crashed_runs_identity_and_mark_the_late_report()
    {
        var record = ServerCrashRecord.From(Nested(), CrashedAt, 500, "dead-session", "client-session", "rider", "1.0");

        var properties = record.ToEventProperties(CrashedAt.AddMinutes(2));

        properties.Should().Contain("SessionId", "dead-session")
            .And.Contain("ClientSessionId", "client-session")
            .And.Contain("IdeClient", "rider")
            .And.Contain("ServerVersion", "1.0")
            .And.Contain("IsFatal", true)
            .And.Contain("ReportedAfterRestart", true)
            .And.Contain("CrashKind", "UnhandledException")
            .And.Contain("CrashAgeMs", 120_000L)
            .And.Contain("UptimeMs", 500L)
            .And.Contain("InnerExceptionCount", 2)
            .And.Contain("InnerExceptionType", "System.IO.IOException");
    }

    [Fact]
    public void A_single_exception_has_no_inner_properties()
    {
        Exception exception;
        try { throw new InvalidOperationException("x"); } catch (Exception ex) { exception = ex; }

        var properties = ServerCrashRecord.From(exception, CrashedAt, null, null, null, null, null).ToEventProperties(CrashedAt);

        properties.Keys.Should().NotContain(k => k.StartsWith("Inner"));
        properties.Keys.Should().NotContain("SessionId").And.NotContain("UptimeMs");
    }
}

public sealed class FatalExceptionRecorderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reqnroll-crash-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private FatalExceptionRecorder Recorder(List<Exception> logged, Func<string?>? ideClient = null) =>
        new(new ServerCrashFiles(_directory), "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00", null,
            ideClient ?? (() => "visualstudio"), "1.0", logged.Add);

    [Fact]
    public void Writes_the_crash_log_and_a_record_for_the_next_start()
    {
        var logged = new List<Exception>();
        var exception = new InvalidOperationException("boom");

        Recorder(logged).Record(exception);

        logged.Should().ContainSingle().Which.Should().BeSameAs(exception);
        var sent = new List<ServerCrashRecord>();
        new ServerCrashFiles(_directory).ReportPending(sent.Add, DateTime.UtcNow);
        sent.Should().ContainSingle().Which.IdeClient.Should().Be("visualstudio");
    }

    [Fact]
    public void An_exception_Main_already_logged_is_not_logged_again()
    {
        var logged = new List<Exception>();
        var recorder = Recorder(logged);
        var exception = new InvalidOperationException("boom");

        recorder.MarkLogged(exception);
        recorder.Record(exception);

        logged.Should().BeEmpty();
        Directory.GetFiles(_directory).Should().ContainSingle();
    }

    [Fact]
    public void Records_only_once()
    {
        var logged = new List<Exception>();
        var recorder = Recorder(logged);

        recorder.Record(new InvalidOperationException("first"));
        recorder.Record(new InvalidOperationException("second"));

        logged.Should().ContainSingle();
    }

    [Fact]
    public void A_failing_ide_lookup_or_crash_log_never_throws_and_still_writes_the_record()
    {
        var recorder = new FatalExceptionRecorder(
            new ServerCrashFiles(_directory), null, null, () => throw new ObjectDisposedException("container"), null,
            _ => throw new IOException("disk full"));

        var act = () => recorder.Record(new InvalidOperationException("boom"));

        act.Should().NotThrow();
        Directory.GetFiles(_directory).Should().ContainSingle();
    }
}

public class UnobservedTaskExceptionReporterTests
{
    [Fact]
    public void Reports_the_single_wrapped_exception_as_non_fatal()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        var inner = new InvalidOperationException("boom");

        new UnobservedTaskExceptionReporter(telemetry).Report(new AggregateException(inner));

        telemetry.Received(1).MonitorError(inner, false);
    }

    [Fact]
    public void Several_wrapped_exceptions_are_reported_as_the_aggregate()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        var aggregate = new AggregateException(new InvalidOperationException("a"), new ArgumentException("b"));

        new UnobservedTaskExceptionReporter(telemetry).Report(aggregate);

        telemetry.Received(1).MonitorError(aggregate, false);
    }

    [Fact]
    public void Reports_are_capped_per_process()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        var sut = new UnobservedTaskExceptionReporter(telemetry);

        for (var i = 0; i < UnobservedTaskExceptionReporter.MaxReports + 5; i++)
            sut.Report(new AggregateException(new InvalidOperationException("x")));

        telemetry.Received(UnobservedTaskExceptionReporter.MaxReports).MonitorError(Arg.Any<Exception>(), false);
    }

    [Fact]
    public void A_throwing_telemetry_service_never_propagates()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        telemetry.When(t => t.MonitorError(Arg.Any<Exception>(), Arg.Any<bool?>())).Do(_ => throw new InvalidOperationException());

        var act = () => new UnobservedTaskExceptionReporter(telemetry).Report(new AggregateException(new Exception()));

        act.Should().NotThrow();
    }
}
