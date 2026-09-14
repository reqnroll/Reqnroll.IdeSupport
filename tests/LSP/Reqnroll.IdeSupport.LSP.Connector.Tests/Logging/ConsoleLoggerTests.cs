using System.Text;
using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Logging;

// Isolates Console.Out/Console.Error redirection per test - xUnit runs tests in the same process,
// and ConsoleLogger writes to the real Console.Out/Error by design (that's how discovery results
// reach the LSP server, which captures this process's stdout).
public class ConsoleLoggerTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    [Fact]
    public void Info_writes_the_message_unchanged_to_stdout()
    {
        // Critical invariant: Runner.PrintResult sends the discovery JSON result to the LSP server
        // via Info(), which the server parses off this process's captured stdout - it must never
        // gain a preamble or any other alteration (issue #628 added exception support to Error,
        // not Info, precisely to avoid touching this channel).
        var sut = new ConsoleLogger();
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        sut.Log(new Log(LogLevel.Info, "{\"result\":true}"));

        stdout.ToString().Should().Be("{\"result\":true}" + Environment.NewLine);
    }

    [Fact]
    public void Error_without_an_exception_writes_the_message_unchanged_to_stderr()
    {
        var sut = new ConsoleLogger();
        var stderr = new StringWriter();
        Console.SetError(stderr);

        sut.Log(new Log(LogLevel.Error, "something went wrong"));

        stderr.ToString().Should().Be("something went wrong" + Environment.NewLine);
    }

    [Fact]
    public void Error_with_an_exception_appends_an_indented_exception_block_to_stderr()
    {
        var sut = new ConsoleLogger();
        var stderr = new StringWriter();
        Console.SetError(stderr);

        sut.Log(new Log(LogLevel.Error, "boom", new InvalidOperationException("bad")));

        var written = stderr.ToString();
        written.Should().Contain("boom").And.Contain("InvalidOperationException").And.Contain("bad");
    }
}
