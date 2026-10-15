#nullable enable

using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class TelemetryScrubberTests
{
    [Theory]
    [InlineData(@"Error reading C:\Users\alice\project\feature.feature", @"Error reading <path>")]
    [InlineData(@"Failed at /home/bob/project/feature.feature", @"Failed at <path>")]
    [InlineData(@"UNC failure \\server\share\file.feature", @"UNC failure <path>")]
    [InlineData("No paths here", "No paths here")]
    [InlineData("", "")]
    public void RedactPaths_replaces_filesystem_paths(string message, string expected)
        => TelemetryScrubber.RedactPaths(message).Should().Be(expected);

    // Adversarial shapes (issue #621): spaces in the user directory, token URLs, relative paths, bare file
    // names, home/env-var forms, two paths in one message, and ordinary text that must survive.
    [Theory]
    [InlineData(@"EACCES C:\Users\John Smith\My Projects\Calc\a.feature denied", "EACCES <path> denied")]
    [InlineData(@"\\srv\my share\dir\f failed", "<path> failed")]
    [InlineData("open /home/john smith/work/x now", "open <path> now")]
    [InlineData(@"ENOENT C:\Users\bob\a.feature and \\srv\share\x and /home/bob/b.cs failed", "ENOENT <path> and <path> and <path> failed")]
    [InlineData("fetch https://example.com/a?token=SECRET123#frag failed", "fetch <url> failed")]
    [InlineData("file:///c:/Users/bob/x.json", "<url>")]
    [InlineData("missing src/Features/Calc.feature and ../shared/x", "missing <path> and <path>")]
    [InlineData("Error in Calculator.feature, Steps.cs and App.csproj", "Error in <path>, <path> and <path>")]
    [InlineData(@"x %USERPROFILE%\proj\a here", "x <path> here")]
    [InlineData("x ~/work/proj/a here", "x <path> here")]
    [InlineData("x $HOME/work/a here", "x <path> here")]
    [InlineData("and/or textDocument/definition System.Text.Json x", "and/or textDocument/definition System.Text.Json x")]
    public void RedactPaths_handles_adversarial_shapes(string message, string expected)
        => TelemetryScrubber.RedactPaths(message).Should().Be(expected);

    [Fact]
    public void ScrubProperties_also_scrubs_StackFrames_and_leaves_clean_frames_untouched()
    {
        var clean = "Reqnroll.IdeSupport.A.B.Go\n[external]\nReqnroll.IdeSupport.A.C.Run";
        var props = new Dictionary<string, object?> { [TelemetryScrubber.StackFramesKey] = clean };
        TelemetryScrubber.ScrubProperties(props)[TelemetryScrubber.StackFramesKey].Should().Be(clean);

        var dirty = new Dictionary<string, object?> { [TelemetryScrubber.StackFramesKey] = @"Go at C:\Users\alice\x.cs" };
        TelemetryScrubber.ScrubProperties(dirty)[TelemetryScrubber.StackFramesKey].Should().Be("Go at <path>");
    }

    [Fact]
    public void RedactPaths_is_idempotent()
    {
        var once = TelemetryScrubber.RedactPaths(@"Could not load C:\Users\alice\x.dll and /home/bob/y.dll");

        TelemetryScrubber.RedactPaths(once).Should().Be(once);
    }

    // Issue #1139: quoted user text (class names, step expressions, feature text, tags, config values) must not leave.
    [Theory]
    [InlineData("Could not load type 'Acme.Payroll.Steps.PaymentSteps' from assembly 'Acme.Payroll, Version=1.0.0.0'", "Could not load type <text> from assembly <text>")]
    [InlineData("parsing \"(Acme payroll total (\\d+)\" - Not enough )'s.", "parsing <text> - Not enough )'s.")]
    [InlineData("(5:3): expected: #EOF, got 'Scenario: Run the secret ACME payroll'", "(5:3): expected: #EOF, got <text>")]
    [InlineData("Invalid tag expression '@acme-secret and': unexpected end", "Invalid tag expression <text>: unexpected end")]
    [InlineData("Invalid regular expression '(acme' was specified", "Invalid regular expression <text> was specified")]
    [InlineData("Could not find file 'C:\\Users\\Jane Doe\\src\\Salary.feature'.", "Could not find file <text>.")]
    [InlineData("value `Acme.Secret` rejected", "value <text> rejected")]
    [InlineData("Index was outside the bounds of the array.", "Index was outside the bounds of the array.")]
    [InlineData("", "")]
    public void ScrubMessage_drops_quoted_user_text(string message, string expected)
        => TelemetryScrubber.ScrubMessage(message).Should().Be(expected);

    [Fact]
    public void ScrubMessage_still_redacts_unquoted_paths_and_urls()
        => TelemetryScrubber.ScrubMessage("Error reading C:\\Users\\alice\\x.feature via https://example.com/a?t=1")
            .Should().Be("Error reading <path> via <url>");

    [Fact]
    public void ScrubMessage_caps_the_length()
    {
        var scrubbed = TelemetryScrubber.ScrubMessage(new string('a', 5000));

        scrubbed!.Length.Should().Be(TelemetryScrubber.MaxMessageLength);
    }

    [Fact]
    public void ScrubMessage_is_idempotent()
    {
        var once = TelemetryScrubber.ScrubMessage("Could not load 'Acme.X' from C:\\Users\\alice\\x.dll");

        TelemetryScrubber.ScrubMessage(once).Should().Be(once);
    }

    [Fact]
    public void ScrubProperties_scrubs_Message_and_ErrorMessage_but_leaves_other_values_alone()
    {
        var props = new Dictionary<string, object?>
        {
            ["Message"] = "Bad 'Acme.Step'",
            ["ErrorMessage"] = "Failed on 'secret feature text'",
            ["Operation"] = "textDocument/definition",
            ["Count"] = 3,
        };

        var scrubbed = TelemetryScrubber.ScrubProperties(props);

        scrubbed["Message"].Should().Be("Bad <text>");
        scrubbed["ErrorMessage"].Should().Be("Failed on <text>");
        scrubbed["Operation"].Should().Be("textDocument/definition");
        scrubbed["Count"].Should().Be(3);
        props["Message"].Should().Be("Bad 'Acme.Step'", "the input dictionary is never mutated");
    }
}
