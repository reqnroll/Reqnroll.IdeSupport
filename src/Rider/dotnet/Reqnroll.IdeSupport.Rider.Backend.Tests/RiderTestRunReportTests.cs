using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Reqnroll.IdeSupport.Rider.Backend.UnitTesting;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

public class RiderTestRunReportTests
{
    [Theory]
    [InlineData("Success", "Passed")]
    [InlineData("Failed", "Failed")]
    [InlineData("Ignored", "Skipped")]
    [InlineData("Running", null)]
    [InlineData("Pending", null)]
    [InlineData("Aborted", null)]
    [InlineData("Inconclusive", null)]
    [InlineData("Unknown", null)]
    [InlineData("None", null)]
    public void Only_finished_statuses_map_to_a_server_outcome(string status, string expected)
    {
        Assert.Equal(expected, RiderTestRunReport.OutcomeName(status));
    }

    [Theory]
    [InlineData("Calculator.feature.cs", true)]
    [InlineData("CALCULATOR.FEATURE.CS", true)]
    [InlineData("Calculator.cs", false)]
    [InlineData("Calculator.feature", false)]
    [InlineData(null, false)]
    public void Recognises_feature_code_behind(string fileName, bool expected)
    {
        Assert.Equal(expected, RiderTestRunReport.IsFeatureCodeBehind(fileName));
    }

    [Fact]
    public void An_assembly_with_the_mtp_hook_type_contains_the_reporter()
    {
        // This test assembly declares a type with the hook's name (below).
        Assert.True(RiderTestRunReport.ContainsMtpReporter(typeof(RiderTestRunReportTests).Assembly.Location));
    }

    [Fact]
    public void An_assembly_without_the_hook_type_does_not_contain_the_reporter()
    {
        Assert.False(RiderTestRunReport.ContainsMtpReporter(typeof(FactAttribute).Assembly.Location));
    }

    [Fact]
    public void A_missing_or_non_assembly_file_does_not_contain_the_reporter()
    {
        Assert.False(RiderTestRunReport.ContainsMtpReporter(null));
        Assert.False(RiderTestRunReport.ContainsMtpReporter(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));

        var notAnAssembly = Path.GetTempFileName();
        try
        {
            File.WriteAllText(notAnAssembly, "not a PE file");
            Assert.False(RiderTestRunReport.ContainsMtpReporter(notAnAssembly));
        }
        finally
        {
            File.Delete(notAnAssembly);
        }
    }

    [Fact]
    public void Stdout_is_cut_at_the_limit()
    {
        var longText = new string('x', RiderTestRunReport.MaxStdoutLength + 10);

        Assert.Equal((null, false), RiderTestRunReport.LimitStdout(null));
        Assert.Equal(("short", false), RiderTestRunReport.LimitStdout("short"));
        var (text, truncated) = RiderTestRunReport.LimitStdout(longText);
        Assert.Equal(RiderTestRunReport.MaxStdoutLength, text.Length);
        Assert.True(truncated);
    }

    [Fact]
    public void Lines_are_hello_then_one_result_per_test_then_run_complete()
    {
        var results = new[]
        {
            new ReportedTestResult
            {
                Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll",
                ManagedType = "Specs.Features.CalculatorFeature",
                ManagedMethod = "AddTwoNumbers",
                DisplayName = "AddTwoNumbers",
                Outcome = "Failed",
                DurationMs = 12.5,
                ErrorMessage = "Expected 3 but was 4",
                ErrorStackTrace = "at Specs.Steps.Then()",
                Stdout = "Given a \"quoted\" step\n-> error: boom\n",
            },
            new ReportedTestResult
            {
                Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll",
                ManagedType = "Specs.Features.CalculatorFeature",
                ManagedMethod = "Subtract",
                DisplayName = "Subtract(\"5\",\"1\",\"0\",null)",
                Outcome = "Passed",
                DurationMs = 1,
            },
        };

        var lines = RiderTestRunReport.Lines("run1", 4242, results);

        Assert.All(lines, line => Assert.EndsWith("\n", line));
        var messages = lines.Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.Equal(new[] { "hello", "result", "result", "runComplete" }, messages.Select(m => m.GetProperty("type").GetString()));

        var hello = messages[0];
        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        Assert.Equal("run1", hello.GetProperty("runId").GetString());
        Assert.Equal(RiderTestRunReport.ReporterKind, hello.GetProperty("reporterKind").GetString());

        var failed = messages[1];
        Assert.Equal(@"C:\repo\Specs\bin\Debug\net8.0\Specs.dll", failed.GetProperty("source").GetString());
        Assert.Equal("Specs.Features.CalculatorFeature", failed.GetProperty("managedType").GetString());
        Assert.Equal("AddTwoNumbers", failed.GetProperty("managedMethod").GetString());
        Assert.Equal("Specs.Features.CalculatorFeature.AddTwoNumbers", failed.GetProperty("fqn").GetString());
        Assert.Equal("Failed", failed.GetProperty("outcome").GetString());
        Assert.Equal(12.5, failed.GetProperty("durationMs").GetDouble());
        Assert.Equal("Expected 3 but was 4", failed.GetProperty("errorMessage").GetString());
        Assert.Equal("Given a \"quoted\" step\n-> error: boom\n", failed.GetProperty("stdout").GetString());
        Assert.False(failed.GetProperty("stdoutTruncated").GetBoolean());

        var passed = messages[2];
        Assert.Equal("Subtract(\"5\",\"1\",\"0\",null)", passed.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, passed.GetProperty("stdout").ValueKind);

        var complete = messages[3];
        Assert.Equal(2, complete.GetProperty("executed").GetInt32());
        Assert.False(complete.GetProperty("aborted").GetBoolean());
    }

    [Fact]
    public void Lines_require_results()
    {
        Assert.Throws<ArgumentNullException>(() => RiderTestRunReport.Lines("run", 1, null));
    }
}
