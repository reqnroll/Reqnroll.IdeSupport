#nullable disable
using System;
using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Pure, VS-independent depth-first walk of a solution's project hierarchy.
/// <para>
/// A solution's project list is flat: solution folders appear alongside real projects, and the
/// projects nested inside a folder are reachable only through that folder's children. Walking the
/// top level alone therefore silently drops every nested project (issue #729).
/// </para>
/// <para>
/// The DTE/COM specifics live in <see cref="VsUtils.GetAllProjects"/>, which supplies the three
/// delegates below; this type holds only the traversal rules so they can be unit-tested without a
/// VS host.
/// </para>
/// </summary>
public static class ProjectHierarchyWalker
{
    /// <summary>
    /// Hard ceiling on nesting depth. Solution folders nest only a handful of levels in practice;
    /// this exists purely so a malformed or cyclic hierarchy cannot recurse forever.
    /// </summary>
    internal const int MaxDepth = 32;

    /// <summary>
    /// Returns every real project reachable from <paramref name="roots"/>, in document order,
    /// descending through container nodes (solution folders) to arbitrary depth.
    /// </summary>
    /// <param name="roots">The solution's top-level nodes: real projects and solution folders.</param>
    /// <param name="getKey">
    /// Returns a node's stable identity (its project file path), or <see langword="null"/>/empty
    /// when the node is not a real project — a solution folder, or one whose path cannot be read.
    /// Such a node is not returned but is still descended into.
    /// </param>
    /// <param name="getChildren">
    /// Returns a node's nested sub-projects, or an empty sequence when it has none. Must not throw;
    /// callers wrap their COM access.
    /// </param>
    /// <remarks>
    /// Nodes are de-duplicated by key, so a project reachable by more than one path is yielded once.
    /// </remarks>
    public static IEnumerable<T> Flatten<T>(
        IEnumerable<T> roots,
        Func<T, string> getKey,
        Func<T, IEnumerable<T>> getChildren)
        where T : class
    {
        if (roots == null)
            yield break;
        if (getKey == null) throw new ArgumentNullException(nameof(getKey));
        if (getChildren == null) throw new ArgumentNullException(nameof(getChildren));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in Walk(roots, getKey, getChildren, seen, depth: 0))
            yield return node;
    }

    private static IEnumerable<T> Walk<T>(
        IEnumerable<T> nodes,
        Func<T, string> getKey,
        Func<T, IEnumerable<T>> getChildren,
        HashSet<string> seen,
        int depth)
        where T : class
    {
        if (depth >= MaxDepth)
            yield break;

        foreach (var node in nodes)
        {
            if (node == null)
                continue;

            // A container (solution folder) has no key: it is not itself a project, but its
            // children are exactly what a flat enumeration would have missed.
            var key = getKey(node);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return node;

            var children = getChildren(node);
            if (children == null)
                continue;

            foreach (var descendant in Walk(children, getKey, getChildren, seen, depth + 1))
                yield return descendant;
        }
    }
}
