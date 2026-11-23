using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Where Reqnroll's MSBuild generation puts a <c>.feature</c> file's code-behind (Reqnroll.Tools.MsBuild.Generation
/// targets, <c>CodeBehindFile</c>):
/// <list type="bullet">
///   <item>by default next to the feature file: <c>%(RelativeDir)%(Filename).feature.cs</c>;</item>
///   <item>with <c>ReqnrollUseIntermediateOutputPathForCodeBehind</c> (Reqnroll 3.3+) under the intermediate output
///   path: <c>$(IntermediateOutputPath)%(NormalizedRelativeDir)%(Filename).feature.cs</c>, where
///   <c>NormalizedRelativeDir</c> is the feature's directory relative to the project with every <c>..</c> replaced
///   by <c>_</c> (so a linked feature outside the project folder still lands inside the intermediate path).</item>
/// </list>
/// </summary>
public static class CodeBehindPaths
{
    /// <summary>The MSBuild property that moves code-behind files to the intermediate output path.</summary>
    public const string UseIntermediateOutputPathProperty = "ReqnrollUseIntermediateOutputPathForCodeBehind";

    /// <summary>The MSBuild property naming the intermediate output path, relative to the project or absolute.</summary>
    public const string IntermediateOutputPathProperty = "IntermediateOutputPath";

    /// <summary>
    /// The full path of the code-behind for the feature file at <paramref name="featurePath"/> in the project whose
    /// directory is <paramref name="projectDirectory"/>; null when it cannot be worked out (no intermediate output
    /// path, or a feature on another drive than the project).
    /// </summary>
    public static string Locate(string projectDirectory, string featurePath, bool useIntermediateOutputPath, string intermediateOutputPath)
    {
        if (string.IsNullOrEmpty(projectDirectory) || string.IsNullOrEmpty(featurePath))
            return null;

        var featureDirectory = Path.GetDirectoryName(featurePath);
        var codeBehindName = Path.GetFileName(featurePath) + ".cs";
        if (featureDirectory == null)
            return null;
        if (!useIntermediateOutputPath)
            return Path.Combine(featureDirectory, codeBehindName);
        if (string.IsNullOrWhiteSpace(intermediateOutputPath))
            return null;

        var relativeDirectory = RelativeDirectory(projectDirectory, featureDirectory);
        if (relativeDirectory == null)
            return null;

        return Path.GetFullPath(Path.Combine(
            projectDirectory, intermediateOutputPath, relativeDirectory.Replace("..", "_"), codeBehindName));
    }

    /// <summary>Whether two full paths name the same file, ignoring separators and case.</summary>
    public static bool SamePath(string left, string right) =>
        left != null && right != null &&
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="directory"/> relative to <paramref name="baseDirectory"/>, as MSBuild's <c>%(RelativeDir)</c>
    /// spells it for an item included by its relative path: empty for the base itself, otherwise ending in a
    /// separator, with <c>..</c> segments to climb out. Null when the two have different roots.
    /// </summary>
    internal static string RelativeDirectory(string baseDirectory, string directory)
    {
        var baseSegments = Segments(baseDirectory);
        var segments = Segments(directory);
        if (baseSegments.Count == 0 || segments.Count == 0 ||
            !string.Equals(baseSegments[0], segments[0], StringComparison.OrdinalIgnoreCase))
            return null;

        var common = 0;
        while (common < baseSegments.Count && common < segments.Count &&
               string.Equals(baseSegments[common], segments[common], StringComparison.OrdinalIgnoreCase))
            common++;

        var parts = Enumerable.Repeat("..", baseSegments.Count - common).Concat(segments.Skip(common)).ToList();
        return parts.Count == 0 ? string.Empty : string.Join(Path.DirectorySeparatorChar.ToString(), parts) + Path.DirectorySeparatorChar;
    }

    private static List<string> Segments(string path)
    {
        var full = Normalize(path);
        var segments = full.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).ToList();
        // Keep a root such as "/" visible as the first segment, so two rooted paths always share it.
        if (full.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            segments.Insert(0, Path.DirectorySeparatorChar.ToString());
        return segments;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
            .TrimEnd(Path.DirectorySeparatorChar);
}
