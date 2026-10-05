#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>
/// Identity of a generated test method as both sides see it: the container the result came from
/// (<c>TestCase.Source</c> / <c>ScenarioTestTarget.OutputAssemblyPath</c>), the declaring type, and the
/// method name <em>without</em> its parameter signature — <c>ManagedMethod</c> carries one
/// (<c>So23(System.String,…)</c>), a CodeLens lookup doesn't, and Reqnroll never generates overloaded
/// scenario methods in one feature class, so the name alone is unambiguous.
/// </summary>
public sealed record TestOutcomeKey(string Source, string TypeFullName, string MethodName)
{
    /// <summary>
    /// Builds a key from a result's identity fields, preferring vstest's row-invariant
    /// <c>ManagedType</c>/<c>ManagedMethod</c> properties and falling back to splitting the FQN for
    /// adapters that don't set them. The source path is normalized once here (issue #515 lesson:
    /// every path comparison in the extension must go through the same routine).
    /// </summary>
    public static TestOutcomeKey? From(string? source, string? managedType, string? managedMethod, string? fullyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        string? type = managedType, method = managedMethod;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(method))
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedName)) return null;
            // NUnit bakes TestCase arguments into row FQNs — strip anything from the first '(' on
            // before locating the type/method split.
            var fqn = StripSignature(fullyQualifiedName!);
            var dot = fqn.LastIndexOf('.');
            if (dot <= 0 || dot == fqn.Length - 1) return null;
            type = fqn.Substring(0, dot);
            method = fqn.Substring(dot + 1);
        }

        return new TestOutcomeKey(PathUtils.NormalizeForComparison(source!), type!, StripSignature(method!));
    }

    /// <summary>Key for a lookup from the CodeLens side (already split identity, unnormalized path).</summary>
    public static TestOutcomeKey ForLookup(string source, string typeFullName, string methodName)
        => new(PathUtils.NormalizeForComparison(source), typeFullName, StripSignature(methodName));

    private static string StripSignature(string name)
    {
        var paren = name.IndexOf('(');
        return paren < 0 ? name : name.Substring(0, paren);
    }

    /// <summary>
    /// Source paths compare case-insensitively (Windows file system; <see cref="PathUtils.NormalizeForComparison"/>
    /// deliberately doesn't fold case), type and method names compare ordinally.
    /// </summary>
    public static IEqualityComparer<TestOutcomeKey> Comparer { get; } = new KeyComparer();

    private sealed class KeyComparer : IEqualityComparer<TestOutcomeKey>
    {
        public bool Equals(TestOutcomeKey? x, TestOutcomeKey? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.TypeFullName, y.TypeFullName, StringComparison.Ordinal)
                   && string.Equals(x.MethodName, y.MethodName, StringComparison.Ordinal);
        }

        public int GetHashCode(TestOutcomeKey obj)
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(obj.TypeFullName);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(obj.MethodName);
                return hash;
            }
        }
    }
}
