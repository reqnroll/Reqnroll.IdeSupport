using System.Linq;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RenameStep;

/// <summary>
/// Client-side parsing of a <c>textDocument/rename</c> JSON result into a
/// <see cref="RenameWorkspaceEdit"/> (<see cref="RenameStepService.ParseWorkspaceEdit"/>).
/// The server applies the edit itself via <c>workspace/applyEdit</c> (see #82); this parsed
/// result is now used only to detect success/failure (a non-null result means the server had
/// an edit to apply) for the rename command's status-bar messaging.
/// </summary>
public class ParseWorkspaceEditTests
{
    private static JObject Edit(int startLine, int startChar, int endLine, int endChar, string newText) => new()
    {
        ["range"] = new JObject
        {
            ["start"] = new JObject { ["line"] = startLine, ["character"] = startChar },
            ["end"]   = new JObject { ["line"] = endLine,   ["character"] = endChar },
        },
        ["newText"] = newText,
    };

    private static JObject WorkspaceEdit(params (string uri, JArray edits)[] files)
    {
        var changes = new JObject();
        foreach (var (uri, edits) in files)
            changes[uri] = edits;
        return new JObject { ["changes"] = changes };
    }

    // ── Null / empty inputs → null ───────────────────────────────────────────────

    [Fact]
    public void A_non_object_result_returns_null()
    {
        RenameStepService.ParseWorkspaceEdit(JValue.CreateNull()).Should().BeNull();
        RenameStepService.ParseWorkspaceEdit(new JArray()).Should().BeNull();
    }

    [Fact]
    public void A_result_without_changes_returns_null()
    {
        RenameStepService.ParseWorkspaceEdit(new JObject()).Should().BeNull();
    }

    [Fact]
    public void An_empty_changes_object_returns_null()
    {
        RenameStepService.ParseWorkspaceEdit(new JObject { ["changes"] = new JObject() }).Should().BeNull();
    }

    [Fact]
    public void A_file_with_an_empty_edit_array_is_dropped_and_yields_null()
    {
        var edit = WorkspaceEdit(("file:///c:/w/Steps.cs", new JArray()));
        RenameStepService.ParseWorkspaceEdit(edit).Should().BeNull();
    }

    // ── Happy path ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_single_file_edit_is_parsed_with_local_path_and_coordinates()
    {
        var edit = WorkspaceEdit(("file:///c:/w/Steps.cs",
            new JArray(Edit(6, 9, 6, 20, "I choose add"))));

        var result = RenameStepService.ParseWorkspaceEdit(edit);

        result.Should().NotBeNull();
        result!.FileEdits.Should().ContainKey(@"c:\w\Steps.cs", "file:/// URIs map to backslash local paths");

        var edits = result.FileEdits[@"c:\w\Steps.cs"];
        edits.Should().ContainSingle();
        edits[0].Should().BeEquivalentTo(new TextEditItem(6, 9, 6, 20, "I choose add"));
    }

    [Fact]
    public void Edits_across_multiple_files_are_grouped_by_local_path()
    {
        var edit = WorkspaceEdit(
            ("file:///c:/w/Steps.cs",          new JArray(Edit(6, 9, 6, 20, "I choose add"))),
            ("file:///c:/w/Calculator.feature", new JArray(Edit(2, 9, 2, 19, "I choose add"))));

        var result = RenameStepService.ParseWorkspaceEdit(edit);

        result.Should().NotBeNull();
        result!.FileEdits.Keys.Should().BeEquivalentTo(new[] { @"c:\w\Steps.cs", @"c:\w\Calculator.feature" });
    }

    [Fact]
    public void Edits_within_a_file_are_sorted_bottom_to_top()
    {
        // Provided top-to-bottom; the applier needs them bottom-to-top so earlier edits
        // do not shift the offsets of later ones.
        var edit = WorkspaceEdit(("file:///c:/w/A.feature", new JArray(
            Edit(1, 4, 1, 8, "first"),
            Edit(3, 4, 3, 8, "second"),
            Edit(3, 0, 3, 2, "zero"))));

        var edits = RenameStepService.ParseWorkspaceEdit(edit)!.FileEdits[@"c:\w\A.feature"];

        edits.Select(e => (e.StartLine, e.StartChar)).Should().ContainInOrder(
            (3, 4), (3, 0), (1, 4));
    }

    // ── Field defaults ───────────────────────────────────────────────────────────

    [Fact]
    public void An_edit_without_a_range_is_skipped()
    {
        var edit = WorkspaceEdit(("file:///c:/w/A.feature", new JArray(
            new JObject { ["newText"] = "no range" },
            Edit(0, 0, 0, 1, "kept"))));

        var edits = RenameStepService.ParseWorkspaceEdit(edit)!.FileEdits[@"c:\w\A.feature"];

        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("kept");
    }

    [Fact]
    public void A_missing_newText_defaults_to_empty_string()
    {
        var noText = new JObject
        {
            ["range"] = new JObject
            {
                ["start"] = new JObject { ["line"] = 0, ["character"] = 0 },
                ["end"]   = new JObject { ["line"] = 0, ["character"] = 3 },
            },
        };
        var edit = WorkspaceEdit(("file:///c:/w/A.feature", new JArray(noText)));

        RenameStepService.ParseWorkspaceEdit(edit)!.FileEdits[@"c:\w\A.feature"][0].NewText.Should().BeEmpty();
    }

    // ── URI mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_non_file_uri_is_passed_through_unchanged_as_the_local_path()
    {
        var edit = WorkspaceEdit(("untitled:Steps.cs", new JArray(Edit(0, 0, 0, 1, "x"))));

        var result = RenameStepService.ParseWorkspaceEdit(edit);

        result.Should().NotBeNull();
        result!.FileEdits.Should().ContainKey("untitled:Steps.cs");
    }
}
