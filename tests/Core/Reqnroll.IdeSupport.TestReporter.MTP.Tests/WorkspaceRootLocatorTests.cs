using Reqnroll.IdeSupport.TestReporter.MTP;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

public class WorkspaceRootLocatorTests
{
    [Fact]
    public void FindNearestRoot_returns_the_starting_directory_when_it_looks_like_a_root()
    {
        var result = WorkspaceRootLocator.FindNearestRoot(@"C:\repo", dir => dir == @"C:\repo");

        result.Should().Be(@"C:\repo");
    }

    [Fact]
    public void FindNearestRoot_walks_up_through_ancestors_until_a_match()
    {
        var result = WorkspaceRootLocator.FindNearestRoot(
            @"C:\repo\tests\Fixture\bin\Debug\net8.0",
            dir => dir == @"C:\repo");

        result.Should().Be(@"C:\repo");
    }

    [Fact]
    public void FindNearestRoot_returns_null_when_nothing_matches_before_the_filesystem_root()
    {
        var result = WorkspaceRootLocator.FindNearestRoot(@"C:\repo\tests\Fixture", _ => false);

        result.Should().BeNull();
    }

    [Fact]
    public void FindNearestRoot_propagates_an_exception_from_looksLikeRoot()
    {
        // FindNearestRoot's own while loop has no try/catch — LooksLikeRoot (the production delegate)
        // is the one responsible for swallowing per-directory failures, exactly like a JsonException
        // must be swallowed inside SessionBreadcrumbMatcher.TryRead rather than by ReadAll's foreach.
        // This just documents that contract for the injected-delegate seam this class is built around.
        Action act = () => WorkspaceRootLocator.FindNearestRoot(@"C:\repo\tests\Fixture", _ => throw new InvalidOperationException());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LooksLikeRoot_is_true_for_a_directory_containing_a_sln_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-workspaceroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "MySolution.sln"), string.Empty);

            WorkspaceRootLocator.LooksLikeRoot(dir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LooksLikeRoot_is_true_for_a_directory_containing_a_git_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-workspaceroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            WorkspaceRootLocator.LooksLikeRoot(dir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LooksLikeRoot_is_true_for_a_directory_containing_a_git_worktree_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-workspaceroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /repo/.git/worktrees/feature");

            WorkspaceRootLocator.LooksLikeRoot(dir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LooksLikeRoot_is_false_for_an_ordinary_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-workspaceroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WorkspaceRootLocator.LooksLikeRoot(dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LooksLikeRoot_returns_false_instead_of_throwing_for_a_path_Directory_EnumerateFiles_rejects()
    {
        // A NUL character makes Directory.EnumerateFiles throw ArgumentException — a type the catch
        // previously did NOT filter for (only IOException/UnauthorizedAccessException were caught), so
        // this path alone used to escape LooksLikeRoot, abort FindNearestRoot's walk-up entirely, and
        // give up on a .sln/.git marker that might sit at an ancestor above it, rather than treating
        // just this one level as "not a root" and letting the walk continue.
        Action act = () => WorkspaceRootLocator.LooksLikeRoot("bad\0path");

        act.Should().NotThrow();
        WorkspaceRootLocator.LooksLikeRoot("bad\0path").Should().BeFalse();
    }
}
