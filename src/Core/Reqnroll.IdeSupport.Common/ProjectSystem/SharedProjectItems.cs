using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>
/// Lists the files a project compiles from the shared projects it imports -- the items of every
/// <c>.projitems</c> named by one of its <c>&lt;Import&gt;</c> elements (issue #736).
/// </summary>
/// <remarks>
/// <para>
/// A shared project's sources compile into each project that imports its <c>.projitems</c>, so
/// they belong to that project's membership baseline. Clients whose file enumeration does not
/// see through the import -- VS's DTE <c>ProjectItems</c> (shared items hang off the shared
/// project's own node) and Rider's folder walk (shared files live outside the project folder) --
/// use this to add them. VS Code does not need it: <c>dotnet msbuild -getItem</c> already expands
/// the import. The Rider plugin carries a Kotlin port of the same rules
/// (<c>SharedProjectItems.kt</c>); keep the two in step.
/// </para>
/// <para>
/// This is a deliberately small reading of MSBuild, not an evaluation. It resolves only
/// <c>$(MSBuildThisFileDirectory)</c>, <c>$(MSBuildThisFileFullPath)</c> and
/// <c>$(MSBuildProjectDirectory)</c> -- which is what Visual Studio writes into both the import and
/// the <c>.projitems</c> -- and skips any path that still carries another property. It honours
/// <c>Include</c>, <c>Exclude</c> and <c>Remove</c> with <c>*</c>, <c>?</c> and <c>**</c>
/// wildcards, and ignores <c>Condition</c>s. Any file it cannot read contributes nothing rather
/// than throwing: missing shared content degrades to the pre-#736 behaviour, never to a failed
/// baseline.
/// </para>
/// </remarks>
public static class SharedProjectItems
{
    // The item types a Reqnroll .feature/.cs file can arrive as; the same list the VS Code client
    // asks `dotnet msbuild -getItem` for (msbuildEvaluator.ts).
    private static readonly HashSet<string> ItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "None", "Content", "ReqnrollFeatureFiles",
    };

    private const string SharedItemsExtension = ".projitems";

    private static readonly Regex UnresolvedProperty = new(@"\$\(", RegexOptions.Compiled);

    /// <summary>
    /// Returns the full paths of the files <paramref name="projectFilePath"/> compiles from the
    /// shared projects it imports, deduplicated case-insensitively, in declaration order. Empty
    /// when it imports none, or when the project file cannot be read.
    /// </summary>
    public static IReadOnlyList<string> GetImportedFiles(string projectFilePath)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projItemsPath in GetSharedItemsImports(projectFilePath))
        {
            foreach (var file in GetItemFiles(projItemsPath))
            {
                if (seen.Add(file))
                    result.Add(file);
            }
        }
        return result;
    }

    /// <summary>Returns the full paths of the existing <c>.projitems</c> files <paramref name="projectFilePath"/> imports.</summary>
    public static IReadOnlyList<string> GetSharedItemsImports(string projectFilePath)
    {
        var document = TryLoad(projectFilePath);
        if (document?.Root is null)
            return Array.Empty<string>();

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? string.Empty;
        var imports = new List<string>();
        foreach (var import in document.Root.Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            var project = (string?)import.Attribute("Project");
            if (string.IsNullOrWhiteSpace(project))
                continue;

            var resolved = ResolvePath(project!, projectDirectory, projectFilePath);
            if (resolved is null ||
                !resolved.EndsWith(SharedItemsExtension, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(resolved))
                continue;

            if (!imports.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                imports.Add(resolved);
        }
        return imports;
    }

    /// <summary>
    /// Returns the full paths of the existing files the <c>Compile</c>/<c>None</c>/<c>Content</c>/
    /// <c>ReqnrollFeatureFiles</c> items of <paramref name="projItemsPath"/> name, after its
    /// <c>Exclude</c>s and <c>Remove</c>s.
    /// </summary>
    public static IReadOnlyList<string> GetItemFiles(string projItemsPath)
    {
        var document = TryLoad(projItemsPath);
        if (document?.Root is null)
            return Array.Empty<string>();

        var fullPath = Path.GetFullPath(projItemsPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        var files = new List<string>();
        foreach (var item in document.Root.Descendants().Where(e => ItemTypes.Contains(e.Name.LocalName)))
        {
            // Only items inside an ItemGroup; a property named e.g. "Content" is not an item.
            if (item.Parent?.Name.LocalName != "ItemGroup")
                continue;

            var include = (string?)item.Attribute("Include");
            var remove = (string?)item.Attribute("Remove");
            if (!string.IsNullOrWhiteSpace(include))
            {
                var excluded = Expand((string?)item.Attribute("Exclude"), directory, fullPath);
                files.AddRange(Expand(include, directory, fullPath)
                                   .Where(f => !excluded.Contains(f, StringComparer.OrdinalIgnoreCase)));
            }
            else if (!string.IsNullOrWhiteSpace(remove))
            {
                var removed = Expand(remove, directory, fullPath);
                files.RemoveAll(f => removed.Contains(f, StringComparer.OrdinalIgnoreCase));
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> Expand(string? itemSpec, string directory, string thisFilePath)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(itemSpec))
            return files;

        foreach (var part in itemSpec!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = ResolvePath(part.Trim(), directory, thisFilePath);
            if (path is null)
                continue;

            if (path.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                if (File.Exists(path))
                    files.Add(path);
                continue;
            }

            files.AddRange(ExpandWildcard(path));
        }
        return files;
    }

    // Enumerates the files under the non-wildcard prefix of `pattern` and keeps those whose path
    // relative to that prefix matches the rest of it.
    private static IEnumerable<string> ExpandWildcard(string pattern)
    {
        var segments = pattern.Split(Path.DirectorySeparatorChar);
        var firstWildcard = Array.FindIndex(segments, s => s.IndexOfAny(new[] { '*', '?' }) >= 0);
        var baseDirectory = string.Join(Path.DirectorySeparatorChar.ToString(), segments.Take(firstWildcard));
        if (baseDirectory.Length == 0 || !Directory.Exists(baseDirectory))
            return Array.Empty<string>();

        var relativePattern = string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(firstWildcard));
        var matcher = new Regex("^" + GlobToRegex(relativePattern) + "$", RegexOptions.IgnoreCase);
        try
        {
            return Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
                            .Where(f => matcher.IsMatch(f.Substring(baseDirectory.Length).TrimStart(Path.DirectorySeparatorChar)))
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GlobToRegex(string glob)
    {
        const string separator = @"[\\/]";
        const string notSeparator = @"[^\\/]";
        var regex = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // `**\` matches zero or more whole directories; a trailing `**` matches anything.
                var followedBySeparator = i + 2 < glob.Length && glob[i + 2] == Path.DirectorySeparatorChar;
                regex.Append(followedBySeparator ? "(?:.*" + separator + ")?" : ".*");
                i += followedBySeparator ? 2 : 1;
            }
            else if (c == '*')
                regex.Append(notSeparator + "*");
            else if (c == '?')
                regex.Append(notSeparator);
            else if (c == Path.DirectorySeparatorChar)
                regex.Append(separator);
            else
                regex.Append(Regex.Escape(c.ToString()));
        }
        return regex.ToString();
    }

    // Substitutes the properties Visual Studio writes into imports and .projitems, normalizes the
    // separators, and roots a relative path at `directory`. Null for a path that still names a
    // property this cannot resolve.
    private static string? ResolvePath(string path, string directory, string thisFilePath)
    {
        var withSeparator = directory.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? directory
            : directory + Path.DirectorySeparatorChar;

        var substituted = ReplaceIgnoreCase(path, "$(MSBuildThisFileDirectory)", withSeparator);
        substituted = ReplaceIgnoreCase(substituted, "$(MSBuildProjectDirectory)", directory);
        substituted = ReplaceIgnoreCase(substituted, "$(MSBuildThisFileFullPath)", thisFilePath);
        if (UnresolvedProperty.IsMatch(substituted))
            return null;

        substituted = substituted.Replace('\\', Path.DirectorySeparatorChar)
                                 .Replace('/', Path.DirectorySeparatorChar);
        try
        {
            var rooted = Path.IsPathRooted(substituted) ? substituted : Path.Combine(directory, substituted);
            // GetFullPath rejects wildcards on .NET Framework, so collapse `..` on the prefix only.
            var wildcard = rooted.IndexOfAny(new[] { '*', '?' });
            if (wildcard < 0)
                return Path.GetFullPath(rooted);

            var prefixEnd = rooted.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
            if (prefixEnd < 0)
                return null;
            return Path.GetFullPath(rooted.Substring(0, prefixEnd)) + rooted.Substring(prefixEnd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ReplaceIgnoreCase(string input, string token, string value)
    {
        var index = input.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            input = input.Substring(0, index) + value + input.Substring(index + token.Length);
            index = input.IndexOf(token, index + value.Length, StringComparison.OrdinalIgnoreCase);
        }
        return input;
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}
