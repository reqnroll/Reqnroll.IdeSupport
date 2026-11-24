using System.Text.Json;
using ReqnrollConnector.Logging;

namespace ReqnrollConnector.AssemblyLoading.dotNET;

/// <summary>
/// A shared framework (for example <c>Microsoft.AspNetCore.App</c>) that the test assembly's
/// <c>runtimeconfig.json</c> asks the .NET host to load, with the minimum version it asks for.
/// </summary>
public sealed record SharedFrameworkReference(string Name, Version Version);

/// <summary>
/// Reads the shared frameworks a test assembly declares in its <c>&lt;name&gt;.runtimeconfig.json</c>.
/// The connector runs as its own app, so the .NET host applies the connector's runtimeconfig, not the
/// test assembly's; this reader lets the connector find the test assembly's frameworks itself.
/// </summary>
public static class RuntimeConfigReader
{
    public static IReadOnlyList<SharedFrameworkReference> ReadFrameworkReferences(string assemblyPath, ILogger log)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            log.Info($"No runtimeconfig.json found at {runtimeConfigPath}; no shared frameworks declared");
            return Array.Empty<SharedFrameworkReference>();
        }

        try
        {
            var frameworks = Parse(File.ReadAllText(runtimeConfigPath));
            log.Info($"Shared frameworks declared in {runtimeConfigPath}: " +
                     (frameworks.Count == 0 ? "none" : string.Join(", ", frameworks.Select(f => $"{f.Name} {f.Version}"))));
            return frameworks;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Info($"Could not read {runtimeConfigPath}: {ex.Message}");
            return Array.Empty<SharedFrameworkReference>();
        }
    }

    /// <summary>
    /// Parses <c>runtimeOptions.framework</c> (one framework) and <c>runtimeOptions.frameworks</c>
    /// (several). Entries without a name or with a version that is not <c>major.minor[.patch]</c>
    /// (optionally followed by a <c>-prerelease</c> suffix) are skipped.
    /// </summary>
    public static IReadOnlyList<SharedFrameworkReference> Parse(string runtimeConfigJson)
    {
        var result = new List<SharedFrameworkReference>();
        using var document = JsonDocument.Parse(runtimeConfigJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) ||
            runtimeOptions.ValueKind != JsonValueKind.Object)
            return result;

        if (runtimeOptions.TryGetProperty("framework", out var framework))
            AddFramework(framework, result);

        if (runtimeOptions.TryGetProperty("frameworks", out var frameworks) &&
            frameworks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in frameworks.EnumerateArray())
                AddFramework(item, result);
        }

        return result;
    }

    private static void AddFramework(JsonElement element, List<SharedFrameworkReference> result)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
            return;

        var name = nameElement.GetString();
        var version = ParseFrameworkVersion(versionElement.GetString());
        if (!IsValidFrameworkName(name) || version == null)
            return;

        result.Add(new SharedFrameworkReference(name!, version));
    }

    // The name becomes a folder name under <dotnet root>/shared, so it must be a single path segment.
    private static bool IsValidFrameworkName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name != "." && name != ".." &&
        name!.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name.IndexOfAny(new[] { '/', '\\' }) < 0;

    /// <summary>
    /// Parses a framework version such as <c>10.0.0</c> or <c>10.0.0-rc.1.25451.107</c>, ignoring
    /// any prerelease suffix. Returns null for anything else.
    /// </summary>
    public static Version? ParseFrameworkVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var dash = value!.IndexOf('-');
        var numericPart = dash >= 0 ? value.Substring(0, dash) : value;
        if (!Version.TryParse(numericPart, out var version))
            return null;

        // Normalize to three parts so 10.0 and 10.0.0 compare equal.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
