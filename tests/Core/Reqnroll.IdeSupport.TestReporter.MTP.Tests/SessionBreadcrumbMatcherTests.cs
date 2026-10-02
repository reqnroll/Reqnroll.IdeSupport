using Reqnroll.IdeSupport.TestReporter.MTP;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

public class SessionBreadcrumbMatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-breadcrumb-matcher-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteBreadcrumb(string fileName, string? endpoint, string? workspaceRoot)
    {
        Directory.CreateDirectory(_dir);
        var endpointJson = endpoint is null ? "null" : $"\"{endpoint}\"";
        var workspaceRootJson = workspaceRoot is null ? "null" : $"\"{workspaceRoot.Replace("\\", "\\\\")}\"";
        File.WriteAllText(Path.Combine(_dir, fileName),
            $$"""{"endpoint":{{endpointJson}},"workspaceRoot":{{workspaceRootJson}},"lspServerPid":1,"startedUtc":"2026-09-20T00:00:00Z"}""");
    }

    [Fact]
    public void ReadAll_returns_empty_when_the_directory_does_not_exist()
        => SessionBreadcrumbMatcher.ReadAll(_dir).Should().BeEmpty();

    [Fact]
    public void ReadAll_parses_endpoint_and_workspace_root()
    {
        WriteBreadcrumb("100.json", "127.0.0.1:1", @"C:\repo");

        var result = SessionBreadcrumbMatcher.ReadAll(_dir);

        result.Should().ContainSingle();
        result[0].Endpoint.Should().Be("127.0.0.1:1");
        result[0].WorkspaceRoot.Should().Be(@"C:\repo");
    }

    [Fact]
    public void ReadAll_skips_a_breadcrumb_missing_an_endpoint()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "100.json"), """{"workspaceRoot":"C:\\repo"}""");

        SessionBreadcrumbMatcher.ReadAll(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ReadAll_skips_a_malformed_json_file_without_throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "100.json"), "{ not json");

        Action act = () => SessionBreadcrumbMatcher.ReadAll(_dir);

        act.Should().NotThrow();
        SessionBreadcrumbMatcher.ReadAll(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ReadAll_skips_one_bad_file_and_still_returns_a_good_one_in_the_same_call()
    {
        // A top-level JSON array (not an object) makes JsonElement.TryGetProperty throw
        // InvalidOperationException — a type TryRead's catch previously did NOT filter for (only
        // IOException/UnauthorizedAccessException/JsonException were caught), so this one file used
        // to escape TryRead, abort ReadAll's whole foreach, and discard every other breadcrumb —
        // including a good one from another concurrently-open IDE window — in the same call.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "100.json"), "[1,2,3]");
        WriteBreadcrumb("200.json", "127.0.0.1:2", @"C:\repo");

        Action act = () => SessionBreadcrumbMatcher.ReadAll(_dir);

        act.Should().NotThrow();
        var result = SessionBreadcrumbMatcher.ReadAll(_dir);
        result.Should().ContainSingle();
        result[0].Endpoint.Should().Be("127.0.0.1:2");
    }

    [Fact]
    public void FindBestMatch_returns_null_when_my_workspace_root_is_null()
        => SessionBreadcrumbMatcher.FindBestMatch(null, [new SessionBreadcrumb("127.0.0.1:1", @"C:\repo")]).Should().BeNull();

    [Fact]
    public void FindBestMatch_returns_null_when_no_candidate_is_an_ancestor()
    {
        var candidates = new[] { new SessionBreadcrumb("127.0.0.1:1", @"C:\other") };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\repo", candidates).Should().BeNull();
    }

    [Fact]
    public void FindBestMatch_matches_an_exact_root()
    {
        var candidates = new[] { new SessionBreadcrumb("127.0.0.1:1", @"C:\repo") };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\repo", candidates)!.Endpoint.Should().Be("127.0.0.1:1");
    }

    [Fact]
    public void FindBestMatch_rejects_a_sibling_folder_that_merely_shares_a_string_prefix()
    {
        // "C:\Repo2" must not be treated as containing "C:\Repo2Extra" style ambiguity, and
        // "C:\Repo" must not match a reporter root of "C:\Repo2" (issue #518-class boundary bug).
        var candidates = new[] { new SessionBreadcrumb("127.0.0.1:1", @"C:\Repo") };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\Repo2\Sub", candidates).Should().BeNull();
    }

    [Fact]
    public void FindBestMatch_deepest_match_wins_for_nested_workspaces()
    {
        // Two LSP server sessions: one for the outer repo, one for a nested sub-solution. Issue #715
        // plan §7 risk #3 — the reporter must pick the deepest (most specific) ancestor.
        var candidates = new[]
        {
            new SessionBreadcrumb("127.0.0.1:1", @"C:\repo"),
            new SessionBreadcrumb("127.0.0.1:2", @"C:\repo\nested-solution"),
        };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\repo\nested-solution\tests\Fixture", candidates)!.Endpoint
            .Should().Be("127.0.0.1:2");
    }

    [Fact]
    public void FindBestMatch_ignores_candidates_with_no_workspace_root()
    {
        var candidates = new[] { new SessionBreadcrumb("127.0.0.1:1", null) };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\repo", candidates).Should().BeNull();
    }

    [Fact]
    public void FindBestMatch_is_case_insensitive()
    {
        var candidates = new[] { new SessionBreadcrumb("127.0.0.1:1", @"c:\repo") };

        SessionBreadcrumbMatcher.FindBestMatch(@"C:\REPO\tests", candidates)!.Endpoint.Should().Be("127.0.0.1:1");
    }
}
