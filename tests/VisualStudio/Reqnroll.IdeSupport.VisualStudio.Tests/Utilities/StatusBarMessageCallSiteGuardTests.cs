using System.IO;
using Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Utilities;

/// <summary>
/// Source-level guard for issue #1021: <c>VsUtils.ShowStatusBarMessage</c> touches the VS status bar
/// and is UI-thread-only, so it must not be called directly from a command's background
/// (<c>ConfigureAwait(false)</c>) continuation. Background callers use
/// <c>ShowStatusBarMessageAsync</c>, which switches to the UI thread first. Scans the checked-in
/// Extension sources, so it needs no VS install.
/// </summary>
public class StatusBarMessageCallSiteGuardTests
{
    // Matches the qualified call only, so the helper's own declaration in VsUtils.cs is not a hit,
    // and excludes ShowStatusBarMessageAsync (the "(" must follow the sync name directly).
    private static readonly Regex SyncCall = new(
        @"VsUtils\s*\.\s*ShowStatusBarMessage\s*\(", RegexOptions.Compiled);

    private static IEnumerable<(string File, int Line, string Code)> ExtensionCodeLines()
    {
        foreach (var file in Directory.EnumerateFiles(
                     RepoPaths.ExtensionProjectDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Trim();
                if (code.Length == 0 || code.StartsWith("//"))
                    continue;
                yield return (file, i + 1, code);
            }
        }
    }

    [Fact]
    public void Background_call_sites_use_the_ui_thread_switching_helper()
    {
        var hits = ExtensionCodeLines()
            .Where(l => SyncCall.IsMatch(l.Code))
            .ToList();

        hits.Should().BeEmpty(
            "VsUtils.ShowStatusBarMessage is UI-thread-only; background continuations must call " +
            "VsUtils.ShowStatusBarMessageAsync, which switches to the UI thread first (issue #1021):{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine,
                hits.Select(h => $"{RepoPaths.Relative(h.File)}:{h.Line}: {h.Code}")));
    }
}
