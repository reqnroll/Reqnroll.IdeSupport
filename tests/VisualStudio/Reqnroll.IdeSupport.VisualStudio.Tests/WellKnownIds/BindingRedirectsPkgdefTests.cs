using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// <c>BindingRedirects.pkgdef</c> hard-codes assembly versions. They go stale silently when a package
/// is upgraded: VS then refuses to load the assembly we ship. This checks each redirect against the
/// assemblies the Extension build actually produced, which sit next to this test's output (the test
/// project references the Extension project). No VS install needed.
/// </summary>
public class BindingRedirectsPkgdefTests
{
    private sealed record Redirect(string Name, Version OldLow, Version OldHigh, Version NewVersion, string CodeBaseFile);

    private static IReadOnlyList<Redirect> ReadRedirects()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.ExtensionProjectDir, "BindingRedirects.pkgdef"));
        return Regex.Split(text, @"^\[", RegexOptions.Multiline)
            .Where(section => section.Contains("bindingRedirection"))
            .Select(section =>
            {
                string Value(string key) => Regex.Match(section, $@"""{key}""=""(?<v>[^""]*)""").Groups["v"].Value;
                var old = Value("oldVersion").Split('-');
                return new Redirect(
                    Value("name"),
                    Version.Parse(old[0]),
                    Version.Parse(old[old.Length - 1]),
                    Version.Parse(Value("newVersion")),
                    Path.GetFileName(Value("codeBase").Replace("$PackageFolder$\\", "")));
            })
            .ToList();
    }

    private static string ShippedAssembly(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        File.Exists(path).Should().BeTrue($"the Extension build should produce {fileName}");
        return path;
    }

    private static IEnumerable<AssemblyName> ReferencesOf(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences
            .Select(handle => reader.GetAssemblyReference(handle).GetAssemblyName())
            .ToList();
    }

    [Fact]
    public void Pkgdef_declares_at_least_one_redirect()
    {
        ReadRedirects().Should().NotBeEmpty();
    }

    [Fact]
    public void Each_redirect_targets_the_version_actually_shipped()
    {
        foreach (var redirect in ReadRedirects())
        {
            AssemblyName.GetAssemblyName(ShippedAssembly(redirect.CodeBaseFile)).Version
                .Should().Be(redirect.NewVersion, $"{redirect.Name}'s newVersion must be the version in the VSIX");
        }
    }

    [Fact]
    public void Each_redirect_covers_the_versions_the_shipped_assemblies_reference()
    {
        var redirects = ReadRedirects();
        var shippedDlls = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(p => Path.GetFileName(p).StartsWith("Microsoft.ApplicationInsights", StringComparison.OrdinalIgnoreCase));

        foreach (var dll in shippedDlls)
        foreach (var reference in ReferencesOf(dll))
        {
            var redirect = redirects.SingleOrDefault(r => r.Name == reference.Name);
            if (redirect is null || reference.Version == redirect.NewVersion)
                continue;

            reference.Version.Should().BeInRange(redirect.OldLow, redirect.OldHigh,
                $"{Path.GetFileName(dll)} references {reference.Name} {reference.Version}, which the pkgdef redirect must cover");
        }
    }
}
