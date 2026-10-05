using System.Globalization;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Reqnroll.IdeSupport.VisualStudio.Extension.Menus;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// Asserts the VS identifiers this extension spells out itself against the VS SDK C headers that
/// define them.
/// </summary>
/// <remarks>
/// The headers come from the <c>Microsoft.VSSDK.BuildTools</c> NuGet package the Extension project
/// already references (<c>tools/vssdk/inc</c>), at the version that project pins. Restoring this
/// test project restores it, so this runs on a CI agent without Visual Studio. The test is skipped,
/// not failed, only if the package is somehow missing from the NuGet cache.
/// </remarks>
public class VsSdkHeaderConstantsTests
{
    private static readonly Lazy<string?> LazyIncludeDir = new(FindIncludeDir);

    private static string? FindIncludeDir()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.ExtensionProjectDir, "Reqnroll.IdeSupport.VisualStudio.Extension.csproj"));
        var version = Regex.Match(csproj, @"Include=""Microsoft\.VSSDK\.BuildTools""\s+Version=""(?<v>[^""]+)""").Groups["v"].Value;
        if (version.Length == 0)
            return null;

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nugetRoot))
            nugetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var dir = Path.Combine(nugetRoot!, "microsoft.vssdk.buildtools", version.ToLowerInvariant(), "tools", "vssdk", "inc");
        return File.Exists(Path.Combine(dir, "vsshlids.h")) ? dir : null;
    }

    private static string ReadHeader(string fileName)
    {
        Skip.If(LazyIncludeDir.Value is null, "Microsoft.VSSDK.BuildTools headers not found in the NuGet cache.");
        return File.ReadAllText(Path.Combine(LazyIncludeDir.Value!, fileName));
    }

    /// <summary>Parses <c>#define NAME { 0x…, 0x…, 0x…, { 0x…×8 } }</c>.</summary>
    internal static Guid DefinedGuid(string header, string name)
    {
        var match = Regex.Match(header, $@"#define\s+{Regex.Escape(name)}\s*\{{(?<body>[^\r\n]*)\}}");
        match.Success.Should().BeTrue($"'{name}' should be #defined as a GUID initializer");
        var parts = Regex.Matches(match.Groups["body"].Value, "0x(?<h>[0-9A-Fa-f]+)")
            .Cast<Match>()
            .Select(m => ulong.Parse(m.Groups["h"].Value, NumberStyles.HexNumber))
            .ToArray();
        parts.Should().HaveCount(11, $"'{name}' should have the 11 parts of a GUID initializer");
        return new Guid((uint)parts[0], (ushort)parts[1], (ushort)parts[2],
            (byte)parts[3], (byte)parts[4], (byte)parts[5], (byte)parts[6],
            (byte)parts[7], (byte)parts[8], (byte)parts[9], (byte)parts[10]);
    }

    /// <summary>Parses <c>#define NAME 0x…</c>.</summary>
    internal static int DefinedInt(string header, string name)
    {
        var match = Regex.Match(header, $@"#define\s+{Regex.Escape(name)}\s+0x(?<h>[0-9A-Fa-f]+)\b");
        match.Success.Should().BeTrue($"'{name}' should be #defined as a hex constant");
        return int.Parse(match.Groups["h"].Value, NumberStyles.HexNumber);
    }

    [SkippableFact]
    public void IDG_VS_CODEWIN_NAVIGATETOLOCATION_matches_vsshlids_h()
    {
        var expected = DefinedInt(ReadHeader("vsshlids.h"), "IDG_VS_CODEWIN_NAVIGATETOLOCATION");

        VsWellKnownIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION.Should().Be(expected);
        ShellMenuIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION.Should().Be(expected);
    }

    [SkippableFact]
    public void GuidSHLMainMenu_matches_vsshlids_h()
    {
        var expected = DefinedGuid(ReadHeader("vsshlids.h"), "guidSHLMainMenu");

        ShellMenuIds.GuidSHLMainMenu.Should().Be(expected);
        VsMenus.guidSHLMainMenu.Should().Be(expected, "the SDK constant and the header should agree");
    }

    [SkippableFact]
    public void Standard_command_set_2K_matches_vsshlids_h()
    {
        VSConstants.VSStd2K.Should().Be(DefinedGuid(ReadHeader("vsshlids.h"), "CMDSETID_StandardCommandSet2K"));
    }

    [SkippableFact]
    public void SolutionExists_ui_context_matches_vsshlids_h()
    {
        Guid.Parse(UIContextGuids80.SolutionExists)
            .Should().Be(DefinedGuid(ReadHeader("vsshlids.h"), "UICONTEXT_SolutionExists"));
    }

    [Fact]
    public void ShellMenuIds_copy_matches_the_SDK_constant()
    {
        // Runs without the headers: the one copy VS.Extensibility forces us to keep must equal the SDK's.
        ShellMenuIds.GuidSHLMainMenu.Should().Be(VsMenus.guidSHLMainMenu);
    }
}
