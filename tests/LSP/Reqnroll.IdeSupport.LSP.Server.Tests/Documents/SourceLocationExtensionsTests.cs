using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

public class SourceLocationExtensionsTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();

    // Temp file written in setup; every test can write its own content.
    private readonly string _tempFile = Path.GetTempFileName();

    public void Dispose() => File.Delete(_tempFile);

    private void Write(string content) => File.WriteAllText(_tempFile, content);

    private SourceLocation At(int line, int col = 1) =>
        new(_tempFile, line, col);

    // ── ExtractSimpleMethodName ───────────────────────────────────────────────

    [Theory]
    [InlineData("ClassName.MethodName",         "MethodName")]
    [InlineData("ClassName.MethodName(int,int)", "MethodName")]
    [InlineData("MethodName",                   "MethodName")]
    [InlineData("A.B.C.MethodName(string)",     "MethodName")]
    public void ExtractSimpleMethodName_extracts_name(string input, string expected) =>
        SourceLocationExtensions.ExtractSimpleMethodName(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExtractSimpleMethodName_returns_null_for_empty_input(string? input) =>
        SourceLocationExtensions.ExtractSimpleMethodName(input).Should().BeNull();

    // ── WithIdentifierLocation — null / empty inputs ──────────────────────────

    [Fact]
    public void WithIdentifierLocation_null_method_returns_original()
    {
        Write("line 1\npublic void Foo() {}\n");
        var loc = At(2);
        loc.WithIdentifierLocation(null, _fileSystem).Should().BeSameAs(loc);
    }

    [Fact]
    public void WithIdentifierLocation_empty_method_returns_original()
    {
        Write("line 1\npublic void Foo() {}\n");
        var loc = At(2);
        loc.WithIdentifierLocation("", _fileSystem).Should().BeSameAs(loc);
    }

    [Fact]
    public void WithIdentifierLocation_inaccessible_file_returns_original()
    {
        var loc = new SourceLocation(@"C:\nonexistent\file.cs", 5, 1);
        loc.WithIdentifierLocation("SomeMethod", _fileSystem).Should().BeSameAs(loc);
    }

    // ── WithIdentifierLocation — method name found on same line ──────────────

    [Fact]
    public void WithIdentifierLocation_finds_method_on_reported_line()
    {
        // Line 1: "[Given(@""..."")] "
        // Line 2: "public void GivenAStep() {"
        // Connector reports line 2 (body start also on line 2 here) — method IS on line 2.
        Write("[Given]\npublic void GivenAStep() {\n");
        var loc = At(line: 2, col: 1);

        var result = loc.WithIdentifierLocation("Steps.GivenAStep(string)", _fileSystem);

        result.SourceFileLine.Should().Be(2);
        result.SourceFileColumn.Should().Be("public void ".Length + 1); // 1-based: col 13
    }

    // ── WithIdentifierLocation — method name found on line above ─────────────

    [Fact]
    public void WithIdentifierLocation_finds_method_above_body_brace()
    {
        // Connector reports line 4 ({), but method identifier is on line 3.
        Write(
            "[Given(@\"a step\")]\n" +
            "public void GivenAStep(\n" +
            "    string value)\n" +
            "{\n");

        // Connector-path body-start is line 4 (the {).
        var loc = At(line: 4, col: 1);

        var result = loc.WithIdentifierLocation("CalculatorSteps.GivenAStep(string)", _fileSystem);

        result.SourceFileLine.Should().Be(2);
        result.SourceFileColumn.Should().BeGreaterThan(1);
    }

    // ── WithIdentifierLocation — method name not in search window ────────────

    [Fact]
    public void WithIdentifierLocation_returns_original_when_name_not_found()
    {
        // 10 lines with no occurrence of "GivenAStep".
        Write(string.Join("\n", Enumerable.Repeat("    // comment", 10)) + "\n");
        var loc = At(line: 7, col: 1);

        // Falls through the 6-line window without a hit → returns original.
        var result = loc.WithIdentifierLocation("GivenAStep", _fileSystem);
        result.Should().BeSameAs(loc);
    }

    // ── WithIdentifierLocation — column and line are 1-based ─────────────────

    [Fact]
    public void WithIdentifierLocation_returns_one_based_line_and_column()
    {
        // "public void MyMethod() {" — col 13 (1-based) for 'M' of MyMethod.
        Write("[Given]\npublic void MyMethod() {\n");
        var loc = At(line: 2);

        var result = loc.WithIdentifierLocation("MyMethod", _fileSystem);

        result.SourceFileLine.Should().Be(2);
        result.SourceFileColumn.Should().Be(13); // 0-based index=12 → 1-based=13
    }
}
