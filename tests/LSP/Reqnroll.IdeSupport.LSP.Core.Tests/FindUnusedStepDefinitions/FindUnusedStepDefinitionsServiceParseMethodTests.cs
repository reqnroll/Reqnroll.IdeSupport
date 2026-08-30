using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.FindUnusedStepDefinitions;

/// <summary>Unit tests for the Method string parser used by F15.</summary>
public class FindUnusedStepDefinitionsServiceParseMethodTests
{
    [Theory]
    [InlineData("StepDefs.GivenSomething()",           "StepDefs",   "GivenSomething")]
    [InlineData("StepDefs.GivenSomething(int, string)", "StepDefs",  "GivenSomething")]
    [InlineData("MyApp.Steps.GivenSomething",           "Steps",     "GivenSomething")]
    [InlineData("A.B.C.MyMethod",                       "C",         "MyMethod")]
    [InlineData("JustAMethod",                          "(unknown)", "JustAMethod")]
    [InlineData("???",                                  "(unknown)", "(unknown)")]
    [InlineData("",                                     "(unknown)", "(unknown)")]
    [InlineData(null,                                   "(unknown)", "(unknown)")]
    public void ParseMethod_returns_expected_class_and_method(
        string? method, string expectedClass, string expectedMethod)
    {
        var (className, methodName) = FindUnusedStepDefinitionsService.ParseMethod(method);
        className.Should().Be(expectedClass);
        methodName.Should().Be(expectedMethod);
    }
}
