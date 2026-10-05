using System.IO;
using System.Xml.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// The hook CodeLens command is registered twice: in <c>HookCodeLensCommands.vsct</c> (what VS loads)
/// and in C# (what the CodeLens popup invokes and the package handles). The VSCT compiler cannot see
/// the C# side, so nothing but this test keeps the two in step. Reads the checked-in file; no VS needed.
/// </summary>
public class VsctConsistencyTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";

    private static XElement LoadVsct() =>
        XDocument.Load(Path.Combine(RepoPaths.ExtensionProjectDir, "HookFeatureCodeLens", "HookCodeLensCommands.vsct")).Root!;

    private static XElement GuidSymbol(string name) =>
        LoadVsct().Descendants(Ns + "GuidSymbol").Single(e => (string)e.Attribute("name") == name);

    private static int IdSymbol(XElement guidSymbol, string name)
    {
        var value = (string)guidSymbol.Elements(Ns + "IDSymbol").Single(e => (string)e.Attribute("name") == name).Attribute("value")!;
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value.Substring(2), 16)
            : int.Parse(value);
    }

    [Fact]
    public void Package_guid_matches_ReqnrollPluginPackage()
    {
        Guid.Parse((string)GuidSymbol("guidReqnrollPluginPackage").Attribute("value")!)
            .Should().Be(Guid.Parse(ReqnrollPluginPackage.PackageGuidString));
    }

    [Fact]
    public void Hook_command_set_matches_HookCodeLensCommandIds()
    {
        Guid.Parse((string)GuidSymbol("guidHookCodeLensCmdSet").Attribute("value")!)
            .Should().Be(HookCodeLensCommandIds.CommandSet);
    }

    [Fact]
    public void Navigate_to_hook_command_id_matches_HookCodeLensCommandIds()
    {
        IdSymbol(GuidSymbol("guidHookCodeLensCmdSet"), "cmdidNavigateToHook")
            .Should().Be(HookCodeLensCommandIds.NavigateToHookCommandId);
    }

    [Fact]
    public void Navigate_to_hook_canonical_name_matches_the_runtime_self_check()
    {
        var vsct = LoadVsct();
        var canonicalName = vsct.Descendants(Ns + "Button")
            .Single(b => (string)b.Attribute("id") == "cmdidNavigateToHook")
            .Descendants(Ns + "CanonicalName").Single().Value;
        var groupParent = vsct.Descendants(Ns + "Group")
            .Single(g => (string)g.Attribute("id") == "HookCodeLensMenuGroup")
            .Element(Ns + "Parent")!;

        // VS names a command after its top-level menu plus its <CanonicalName> (confirmed live:
        // "Tools.Reqnroll.NavigateToHook"), so the expected name follows the group's parent menu.
        ((string)groupParent.Attribute("id")).Should().Be("IDM_VS_MENU_TOOLS");
        VsWellKnownIdsSelfCheck.ExpectedCommands
            .Single(c => c.Group == HookCodeLensCommandIds.CommandSet && c.Id == HookCodeLensCommandIds.NavigateToHookCommandId)
            .ExpectedName.Should().Be("Tools." + canonicalName);
    }
}
