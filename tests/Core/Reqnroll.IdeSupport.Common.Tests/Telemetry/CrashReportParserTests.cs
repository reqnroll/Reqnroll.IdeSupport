using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

// The samples are what the .NET 10 runtime printed to stderr for each kind of crash (captured from a throwaway
// program in a Reqnroll.IdeSupport namespace), with the source paths shortened.
public class CrashReportParserTests
{
    private const string NestedUnhandled = """
        Unhandled exception. System.InvalidOperationException: Outer failed for 'MyStep'
         ---> System.ApplicationException: middle
        second line
         ---> System.IO.DirectoryNotFoundException: Could not find a part of the path 'C:\nope\missing.feature'.
           at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options)
           at System.IO.File.ReadAllText(String path, Encoding encoding)
           at Reqnroll.IdeSupport.CrashGen.Worker.Inner() in C:\Users\me\src\crashgen\Program.cs:line 31
           --- End of inner exception stack trace ---
           at Reqnroll.IdeSupport.CrashGen.Worker.Inner() in C:\Users\me\src\crashgen\Program.cs:line 32
           at Reqnroll.IdeSupport.CrashGen.Worker.Run() in C:\Users\me\src\crashgen\Program.cs:line 25
           --- End of inner exception stack trace ---
           at Reqnroll.IdeSupport.CrashGen.Worker.Run() in C:\Users\me\src\crashgen\Program.cs:line 26
           at Reqnroll.IdeSupport.CrashGen.Program.Main(String[] args) in C:\Users\me\src\crashgen\Program.cs:line 9
           at Reqnroll.IdeSupport.CrashGen.Program.<Main>(String[] args)
        """;

    private const string GenericAsyncUnhandled = """
        Unhandled exception. System.ArgumentException: bad 1
           at Reqnroll.IdeSupport.CrashGen.Worker.Helper[T](T value) in C:\Users\me\src\crashgen\Program.cs:line 41
           at Reqnroll.IdeSupport.CrashGen.Worker.RunAsync() in C:\Users\me\src\crashgen\Program.cs:line 38
        """;

    private const string LambdaOnThread = """
        Unhandled exception. System.NullReferenceException: Object reference not set to an instance of an object.
           at Reqnroll.IdeSupport.CrashGen.Worker.<>c.<Lambda>b__4_0() in C:\Users\me\src\crashgen\Program.cs:line 45
           at Reqnroll.IdeSupport.CrashGen.Worker.Lambda() in C:\Users\me\src\crashgen\Program.cs:line 46
           at System.Threading.Thread.StartCallback()
        """;

    private const string StackOverflow = """
        Stack overflow.
        Repeated 24085 times:
        --------------------------------
           at Reqnroll.IdeSupport.CrashGen.Program.Recurse(Int32)
        --------------------------------
           at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()
           at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[[System.__Canon, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]](System.__Canon ByRef)
           at Reqnroll.IdeSupport.CrashGen.Program.Main(System.String[])
        """;

    private const string FailFast = """
        Process terminated.
        Giving up on "C:\Users\me\secret.feature"
           at System.Environment.FailFast(System.String)
           at Reqnroll.IdeSupport.CrashGen.Program+<Main>d__0.MoveNext()
        """;

    private static CrashReport? Parse(string text, string? before = null)
    {
        var lines = new List<string>();
        if (before is not null)
            lines.AddRange(before.Split('\n'));
        lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        return CrashReportParser.Parse(lines);
    }

    [Fact]
    public void Nested_exceptions_report_the_outer_and_the_innermost_exception()
    {
        var report = Parse(NestedUnhandled)!;

        report.Kind.Should().Be(CrashKind.UnhandledException);
        report.Outer.Type.Should().Be("System.InvalidOperationException");
        report.Outer.Message.Should().Be("Outer failed for <text>");
        report.Outer.StackFrames.Should().Be(
            "Reqnroll.IdeSupport.CrashGen.Worker.Run:26\n" +
            "Reqnroll.IdeSupport.CrashGen.Program.Main:9\n" +
            "Reqnroll.IdeSupport.CrashGen.Program.Main");
        report.InnerExceptionCount.Should().Be(2);
        report.Inner!.Type.Should().Be("System.IO.DirectoryNotFoundException");
        report.Inner.Message.Should().Be("Could not find a part of the path <text>.");
        report.Inner.StackFrames.Should().Be("[external]\nReqnroll.IdeSupport.CrashGen.Worker.Inner:31");
    }

    [Fact]
    public void No_path_or_parameter_list_reaches_the_properties()
    {
        var properties = Parse(NestedUnhandled)!.ToProperties();

        foreach (var value in properties.Values.OfType<string>())
        {
            value.Should().NotContain(@"C:\");
            value.Should().NotContain("(");
            value.Should().NotContain("MyStep");
        }
    }

    [Fact]
    public void Properties_carry_the_report_under_the_documented_keys()
    {
        var properties = Parse(NestedUnhandled)!.ToProperties();

        properties.Keys.Should().BeEquivalentTo(
            "CrashKind", "ExceptionType", "Message", "StackFrames",
            "InnerExceptionCount", "InnerExceptionType", "InnerMessage", "InnerStackFrames");
        properties["InnerExceptionCount"].Should().Be(2);
    }

    [Fact]
    public void A_single_exception_has_no_inner_properties()
    {
        var report = Parse(GenericAsyncUnhandled)!;

        report.Inner.Should().BeNull();
        report.Outer.StackFrames.Should().Be(
            "Reqnroll.IdeSupport.CrashGen.Worker.Helper:41\nReqnroll.IdeSupport.CrashGen.Worker.RunAsync:38");
        report.ToProperties().Keys.Should().BeEquivalentTo("CrashKind", "ExceptionType", "Message", "StackFrames");
    }

    [Fact]
    public void Compiler_generated_lambda_frames_are_normalised()
    {
        Parse(LambdaOnThread)!.Outer.StackFrames.Should().Be(
            "Reqnroll.IdeSupport.CrashGen.Worker.Lambda{lambda}:45\n" +
            "Reqnroll.IdeSupport.CrashGen.Worker.Lambda:46\n" +
            "[external]");
    }

    [Fact]
    public void A_stack_overflow_reports_its_frames_without_an_exception()
    {
        var report = Parse(StackOverflow)!;

        report.Kind.Should().Be(CrashKind.StackOverflow);
        report.Outer.Type.Should().BeNull();
        report.Outer.Message.Should().BeNull();
        report.Outer.StackFrames.Should().Be(
            "Reqnroll.IdeSupport.CrashGen.Program.Recurse\n" +
            "Reqnroll.IdeSupport.CrashGen.Program.Main\n" +
            "[external]\n" +
            "Reqnroll.IdeSupport.CrashGen.Program.Main");
        report.ToProperties().Keys.Should().BeEquivalentTo("CrashKind", "StackFrames");
    }

    [Fact]
    public void FailFast_reports_its_scrubbed_message()
    {
        var report = Parse(FailFast)!;

        report.Kind.Should().Be(CrashKind.FailFast);
        report.Outer.Type.Should().BeNull();
        report.Outer.Message.Should().Be("Giving up on <text>");
        report.Outer.StackFrames.Should().Be("[external]\nReqnroll.IdeSupport.CrashGen.Program.Main");
    }

    [Fact]
    public void Only_the_last_report_is_parsed_and_earlier_stderr_is_ignored()
    {
        var report = Parse(GenericAsyncUnhandled, before: "warn: something\n" + LambdaOnThread.Replace("\r\n", "\n"))!;

        report.Outer.Type.Should().Be("System.ArgumentException");
    }

    [Theory]
    [InlineData("just some warning")]
    [InlineData("")]
    public void Text_without_a_crash_header_has_no_report(string text)
    {
        Parse(text).Should().BeNull();
    }

    [Fact]
    public void Null_lines_have_no_report()
    {
        CrashReportParser.Parse(null!).Should().BeNull();
    }

    [Fact]
    public void A_foreign_namespace_reusing_the_product_prefix_is_not_a_product_frame()
    {
        var report = Parse("""
            Unhandled exception. System.Exception: x
               at Reqnroll.IdeSupportLookalike.Evil.Run() in C:\x.cs:line 1
            """)!;

        report.Outer.StackFrames.Should().BeNull();
    }

    [Theory]
    [InlineData("Reqnroll.IdeSupport.X.Worker..ctor(String name) in C:\\x.cs:line 3", "Reqnroll.IdeSupport.X.Worker.{ctor}:3")]
    [InlineData("Reqnroll.IdeSupport.X.Worker..cctor()", "Reqnroll.IdeSupport.X.Worker.{cctor}")]
    [InlineData("Reqnroll.IdeSupport.X.Outer+Inner.Run()", "Reqnroll.IdeSupport.X.Outer.Inner.Run")]
    [InlineData("Reqnroll.IdeSupport.X.Box`1.Get()", "Reqnroll.IdeSupport.X.Box.Get")]
    public void Constructors_nested_and_generic_types_are_normalised(string frame, string expected)
    {
        var report = Parse("Unhandled exception. System.Exception: x\n   at " + frame)!;

        report.Outer.StackFrames.Should().Be(expected);
    }

    [Fact]
    public void A_header_that_is_not_a_type_name_keeps_no_type()
    {
        var report = Parse("Fatal error. 0xC0000005\n   at Reqnroll.IdeSupport.X.Y()")!;

        report.Kind.Should().Be(CrashKind.FatalError);
        report.Outer.Type.Should().BeNull();
        report.Outer.Message.Should().Be("0xC0000005");
        report.Outer.StackFrames.Should().Be("Reqnroll.IdeSupport.X.Y");
    }
}

public class CrashReportCollectorTests
{
    [Fact]
    public void Lines_before_a_crash_header_are_not_kept()
    {
        var collector = new CrashReportCollector();
        collector.OnLine("warn: unrelated");

        collector.Parse().Should().BeNull();
    }

    [Fact]
    public void A_report_written_line_by_line_is_parsed()
    {
        var collector = new CrashReportCollector();
        collector.OnLine("warn: unrelated");
        collector.OnLine("Unhandled exception. System.InvalidOperationException: boom");
        collector.OnLine("   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line 7");

        var report = collector.Parse()!;

        report.Outer.Type.Should().Be("System.InvalidOperationException");
        report.Outer.StackFrames.Should().Be("Reqnroll.IdeSupport.X.Y.Run:7");
    }

    [Fact]
    public void The_number_and_length_of_kept_lines_are_bounded()
    {
        var collector = new CrashReportCollector();
        collector.OnLine("Unhandled exception. System.Exception: " + new string('x', 10_000));
        for (var i = 0; i < CrashReportCollector.MaxLines + 50; i++)
            collector.OnLine("   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line " + (i + 1));

        var report = collector.Parse()!;

        report.Outer.Message!.Length.Should().BeLessThanOrEqualTo(TelemetryScrubber.MaxMessageLength);
        report.Outer.StackFrames.Should().StartWith("Reqnroll.IdeSupport.X.Y.Run:1\n");
    }

    [Fact]
    public void WaitForEnd_returns_once_the_stream_has_ended()
    {
        var collector = new CrashReportCollector();

        collector.WaitForEnd(TimeSpan.Zero).Should().BeFalse();
        collector.OnLine(null!);
        collector.WaitForEnd(TimeSpan.Zero).Should().BeTrue();
    }
}
