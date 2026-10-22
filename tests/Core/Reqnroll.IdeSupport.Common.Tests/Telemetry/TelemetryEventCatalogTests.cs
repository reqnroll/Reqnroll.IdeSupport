using System.Reflection;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

/// <summary>
/// Pins the event-name scheme and keeps the hand-kept mirrors of
/// <see cref="TelemetryEvents"/> (VS Code, Rider, the KQL legacy-name map) from drifting.
/// </summary>
public class TelemetryEventCatalogTests
{
    // Category.Name or Category.Thing.Action, PascalCase segments.
    private static readonly Regex HierarchicalName = new(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$");

    private static readonly (string Constant, string Name)[] Catalog = typeof(TelemetryEvents)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
        .ToArray();

    private static readonly string[] CatalogNames = Catalog.Select(c => c.Name).ToArray();

    [Fact]
    public void Every_event_name_is_hierarchical_and_unique()
    {
        CatalogNames.Should().NotBeEmpty();
        CatalogNames.Should().OnlyContain(n => HierarchicalName.IsMatch(n));
        CatalogNames.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_constant_is_named_after_its_event_with_the_dots_removed()
    {
        foreach (var (constant, name) in Catalog)
            constant.Should().Be(name.Replace(".", ""), $"the constant for \"{name}\" is named after the event");
    }

    [Fact]
    public void VsCode_catalog_names_all_exist_in_the_dotnet_catalog_under_the_same_name()
    {
        var ts = ReadRepoFile("src/VSCode/src/telemetryEvents.ts");
        var block = Regex.Match(ts, @"export const TelemetryEvents = \{(.*?)\n\} as const;", RegexOptions.Singleline);
        block.Success.Should().BeTrue("the TelemetryEvents object literal is how the catalog is mirrored");

        var entries = Regex.Matches(block.Groups[1].Value, @"(\w+):\s*'([^']+)'")
            .Select(m => (Key: m.Groups[1].Value, Name: m.Groups[2].Value))
            .ToList();

        entries.Should().NotBeEmpty();
        entries.Select(e => e.Name).Should().BeSubsetOf(CatalogNames);
        foreach (var (key, name) in entries)
        {
            var dotless = name.Replace(".", "");
            key.Should().Be(char.ToLowerInvariant(dotless[0]) + dotless[1..],
                $"the TypeScript key for \"{name}\" is the event name with the dots removed, camelCased");
        }
    }

    [Fact]
    public void Rider_catalog_names_all_exist_in_the_dotnet_catalog_under_the_same_name()
    {
        var kt = ReadRepoFile("src/Rider/src/main/kotlin/net/reqnroll/idesupport/rider/telemetry/RiderTelemetryTransmitter.kt");

        var entries = Regex.Matches(kt, @"const val ([A-Z_]+) = ""([^""]+)""")
            .Select(m => (Key: m.Groups[1].Value, Name: m.Groups[2].Value))
            .Where(e => HierarchicalName.IsMatch(e.Name) && !e.Name.StartsWith("net.reqnroll", StringComparison.Ordinal))
            .ToList();

        entries.Should().NotBeEmpty();
        entries.Select(e => e.Name).Should().BeSubsetOf(CatalogNames);
        foreach (var (key, name) in entries)
            key.Should().Be(
                Regex.Replace(name.Replace(".", ""), "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant(),
                $"the Kotlin constant for \"{name}\" is the event name with the dots removed, in upper snake case");
    }

    [Fact]
    public void Kql_legacy_name_map_targets_current_names_and_never_maps_a_current_name()
    {
        var kql = ReadRepoFile("telemetry/event-name-map.kql");

        var pairs = Regex.Matches(kql, @"^\s*""([^""]+)"": ""([^""]+)"",?\s*$", RegexOptions.Multiline)
            .Select(m => (Legacy: m.Groups[1].Value, Current: m.Groups[2].Value))
            .ToList();

        pairs.Should().NotBeEmpty();
        pairs.Select(p => p.Current).Should().BeSubsetOf(CatalogNames);
        pairs.Select(p => p.Legacy).Should().NotIntersectWith(CatalogNames);
        pairs.Select(p => p.Legacy).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_kql_query_embeds_the_same_legacy_name_map_as_the_shared_file()
    {
        var map = ReadRepoFile("telemetry/event-name-map.kql").ReplaceLineEndings("\n").TrimEnd();
        var queries = Directory.GetFiles(Path.Combine(RepoRoot(), "telemetry", "queries"), "*.kql");

        queries.Should().NotBeEmpty();
        foreach (var query in queries)
            File.ReadAllText(query).ReplaceLineEndings("\n").Should()
                .Contain(map, $"{Path.GetFileName(query)} must embed telemetry/event-name-map.kql verbatim");
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reqnroll.IdeSupport.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Repo root (Reqnroll.IdeSupport.slnx) not found above " + AppContext.BaseDirectory);
    }
}
