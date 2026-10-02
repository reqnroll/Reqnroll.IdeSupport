using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.ProjectSystem;

/// <summary>
/// Traversal rules behind <c>VsUtils.GetAllProjects</c> (issue #729): a flat walk of the solution's
/// top level drops every project nested in a solution folder, which left the LSP server with no
/// binding registry for those projects.
/// </summary>
public class ProjectHierarchyWalkerTests
{
    /// <summary>A stand-in for a DTE <c>Project</c>: a real project has a path, a solution folder does not.</summary>
    private sealed class Node
    {
        public string Path { get; init; }
        public List<Node> Children { get; } = new();

        public static Node Project(string path) => new() { Path = path };
        public static Node Folder(params Node[] children)
        {
            var folder = new Node { Path = null };
            folder.Children.AddRange(children);
            return folder;
        }
    }

    private static IEnumerable<string> Flatten(params Node[] roots) =>
        ProjectHierarchyWalker
            .Flatten(roots, n => n.Path, n => n.Children)
            .Select(n => n.Path);

    [Fact]
    public void Returns_top_level_projects()
    {
        Flatten(Node.Project("a.csproj"), Node.Project("b.csproj"))
            .Should().Equal("a.csproj", "b.csproj");
    }

    [Fact]
    public void Returns_projects_nested_in_a_solution_folder()
    {
        // The #729 regression case: SmokeTests lived under a "Tests" solution folder and was
        // never sent to the server.
        var solution = new[]
        {
            Node.Project("Core.csproj"),
            Node.Folder(Node.Project("SmokeTests.csproj"))
        };

        Flatten(solution).Should().Equal("Core.csproj", "SmokeTests.csproj");
    }

    [Fact]
    public void Does_not_return_the_solution_folder_itself()
    {
        Flatten(Node.Folder(Node.Project("Nested.csproj")))
            .Should().Equal("Nested.csproj");
    }

    [Fact]
    public void Descends_through_nested_solution_folders()
    {
        var deep = Node.Folder(Node.Folder(Node.Folder(Node.Project("Deep.csproj"))));

        Flatten(deep).Should().Equal("Deep.csproj");
    }

    [Fact]
    public void Returns_projects_from_several_folders_in_document_order()
    {
        var solution = new[]
        {
            Node.Folder(Node.Project("one.csproj")),
            Node.Project("two.csproj"),
            Node.Folder(Node.Project("three.csproj"), Node.Project("four.csproj"))
        };

        Flatten(solution).Should().Equal("one.csproj", "two.csproj", "three.csproj", "four.csproj");
    }

    [Fact]
    public void A_project_that_also_has_children_is_returned_along_with_them()
    {
        // Walker contract: having a key and having children are independent. The VS adapter only
        // reports children for solution folders, but the traversal itself does not assume that.
        var parent = Node.Project("parent.csproj");
        parent.Children.Add(Node.Project("child.csproj"));

        Flatten(parent).Should().Equal("parent.csproj", "child.csproj");
    }

    [Fact]
    public void Deduplicates_a_project_reachable_by_more_than_one_path()
    {
        var shared = Node.Project("shared.csproj");

        Flatten(Node.Folder(shared), Node.Folder(shared))
            .Should().Equal("shared.csproj");
    }

    [Fact]
    public void Deduplication_ignores_path_case()
    {
        Flatten(Node.Project(@"C:\ws\A.csproj"), Node.Project(@"c:\WS\a.csproj"))
            .Should().Equal(@"C:\ws\A.csproj");
    }

    [Fact]
    public void Skips_nodes_with_a_blank_path_without_skipping_their_children()
    {
        // A project whose FullName threw is reported as keyless, exactly like a solution folder.
        var keyless = new Node { Path = "   " };
        keyless.Children.Add(Node.Project("child.csproj"));

        Flatten(keyless).Should().Equal("child.csproj");
    }

    [Fact]
    public void Tolerates_null_nodes_and_null_child_collections()
    {
        var roots = new[] { null, Node.Project("a.csproj") };

        ProjectHierarchyWalker.Flatten(roots, n => n.Path, _ => null)
            .Select(n => n.Path)
            .Should().Equal("a.csproj");
    }

    [Fact]
    public void Null_roots_yield_nothing()
    {
        ProjectHierarchyWalker.Flatten<Node>(null, n => n.Path, n => n.Children)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_cycle_terminates_instead_of_recursing_forever()
    {
        var a = Node.Project("a.csproj");
        var b = Node.Project("b.csproj");
        a.Children.Add(b);
        b.Children.Add(a);   // malformed hierarchy

        var result = Flatten(a).ToList();

        result.Should().Equal("a.csproj", "b.csproj");
    }

    [Fact]
    public void A_cycle_of_keyless_containers_terminates()
    {
        // Nothing is de-duplicated here (no keys), so only the depth ceiling can stop the walk.
        var outer = Node.Folder();
        outer.Children.Add(outer);

        ProjectHierarchyWalker.Flatten(new[] { outer }, n => n.Path, n => n.Children)
            .Should().BeEmpty();
    }

    [Fact]
    public void Stops_descending_past_the_depth_ceiling()
    {
        // Build a folder chain one level deeper than the walker will follow.
        var node = Node.Project("too-deep.csproj");
        for (var i = 0; i < ProjectHierarchyWalker.MaxDepth; i++)
            node = Node.Folder(node);

        Flatten(node).Should().BeEmpty();
    }

    [Fact]
    public void Null_delegates_are_rejected()
    {
        var roots = new[] { Node.Project("a.csproj") };

        Assert.Throws<ArgumentNullException>(
            () => ProjectHierarchyWalker.Flatten(roots, null, n => n.Children).ToList());
        Assert.Throws<ArgumentNullException>(
            () => ProjectHierarchyWalker.Flatten(roots, n => n.Path, null).ToList());
    }
}
