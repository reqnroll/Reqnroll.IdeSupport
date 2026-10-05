using System;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Unit tests for <see cref="RunTestCodeLensService.CollectContainerNodes"/> — the Feature/Rule
/// node walk backing the "Run scenarios" CodeLens (issue #744). Pure symbol-tree logic, so it's
/// tested directly against hand-built <see cref="GherkinSymbolNode"/> trees, the same way
/// <see cref="ScenarioTestTargetService.MapResult"/> and other pure mapping helpers are tested
/// elsewhere in this project.
/// </summary>
public class RunTestCodeLensServiceContainerNodesTests
{
    private const int ModuleKind = 2;    // LSP SymbolKind.Module — Feature
    private const int NamespaceKind = 3; // LSP SymbolKind.Namespace — Rule
    private const int MethodKind = 6;    // LSP SymbolKind.Method — Scenario/Scenario Outline

    private static GherkinSymbolRange Range(int startLine, int endLine) =>
        new(new GherkinSymbolPosition(startLine, 0), new GherkinSymbolPosition(endLine, 0));

    private static GherkinSymbolNode Node(
        string name, int kind, int startLine, int endLine, params GherkinSymbolNode[] children) =>
        new(name, kind, Range(startLine, endLine), Range(startLine, startLine), children, Detail: null);

    [Fact]
    public void Collects_the_root_Feature_node()
    {
        var feature = Node("F", ModuleKind, 0, 5,
            Node("Scenario A", MethodKind, 1, 2));

        var result = RunTestCodeLensService.CollectContainerNodes(new[] { feature });

        result.Should().ContainSingle().Which.Name.Should().Be("F");
    }

    [Fact]
    public void Collects_nested_Rule_nodes_alongside_the_Feature()
    {
        var rule = Node("R", NamespaceKind, 3, 5,
            Node("Scenario Inside", MethodKind, 4, 5));
        var feature = Node("F", ModuleKind, 0, 5,
            Node("Scenario Outside", MethodKind, 1, 2),
            rule);

        var result = RunTestCodeLensService.CollectContainerNodes(new[] { feature });

        result.Select(n => n.Name).Should().BeEquivalentTo(new[] { "F", "R" });
    }

    [Fact]
    public void Does_not_collect_Scenario_or_Scenario_Outline_nodes()
    {
        var feature = Node("F", ModuleKind, 0, 5,
            Node("Scenario A", MethodKind, 1, 2),
            Node("Scenario Outline B", MethodKind, 3, 4));

        var result = RunTestCodeLensService.CollectContainerNodes(new[] { feature });

        result.Should().ContainSingle().Which.Kind.Should().Be(ModuleKind);
    }

    [Fact]
    public void Multiple_Rules_are_all_collected()
    {
        var feature = Node("F", ModuleKind, 0, 10,
            Node("R1", NamespaceKind, 1, 4, Node("S1", MethodKind, 2, 3)),
            Node("R2", NamespaceKind, 5, 9, Node("S2", MethodKind, 6, 7)));

        var result = RunTestCodeLensService.CollectContainerNodes(new[] { feature });

        result.Select(n => n.Name).Should().BeEquivalentTo(new[] { "F", "R1", "R2" });
    }

    [Fact]
    public void Empty_symbol_list_returns_no_containers()
    {
        RunTestCodeLensService.CollectContainerNodes(Array.Empty<GherkinSymbolNode>()).Should().BeEmpty();
    }
}
