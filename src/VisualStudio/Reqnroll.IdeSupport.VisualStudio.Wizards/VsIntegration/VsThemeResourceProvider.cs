using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Builds a dictionary of themed WPF resources from the current VS theme.
/// Lives in the VsIntegration layer so VS SDK types never leak into the UI project.
/// </summary>
internal static class VsThemeResourceProvider
{
    public static IReadOnlyDictionary<string, object> GetThemedResources()
    {
        var dict = new Dictionary<string, object>();

        AddThemedBrush(dict, ThemedDialogColors.WindowPanelBrushKey, "ThemedWindowBackgroundBrush");
        AddThemedBrush(dict, ThemedDialogColors.WindowButtonBrushKey);
        AddThemedBrush(dict, ThemedDialogColors.WindowPanelTextBrushKey);
        AddThemedBrush(dict, ThemedDialogColors.WindowButtonGlyphBrushKey);
        AddThemedBrush(dict, ThemedDialogColors.HeaderTextBrushKey);
        AddThemedBrush(dict, ThemedDialogColors.WindowButtonHoverBrushKey);
        AddThemedBrush(dict, ThemedDialogColors.WindowButtonHoverGlyphBrushKey, "ThemedWindowButtonHoverForegroundBrushKey");
        AddThemedBrush(dict, ThemedDialogColors.WindowButtonHoverBorderBrushKey);
        AddThemedBrushFromColorKey(dict, ThemedDialogColors.CloseWindowButtonHoverColorKey, "ThemedCloseWindowButtonHoverBrushKey");
        AddThemedBrushFromColorKey(dict, ThemedDialogColors.CloseWindowButtonHoverTextColorKey, "ThemedCloseWindowButtonHoverTextBrushKey");

        AddThemedStyle(dict, VsResourceKeys.TextBlockEnvironment283PercentFontSizeStyleKey, "ThemedHeader");
        AddThemedStyle(dict, VsResourceKeys.ThemedDialogLabelStyleKey);
        AddThemedStyle(dict, VsResourceKeys.ThemedDialogCheckBoxStyleKey);
        AddThemedStyle(dict, VsResourceKeys.ThemedDialogComboBoxStyleKey);
        AddThemedStyle(dict, VsResourceKeys.ThemedDialogButtonStyleKey);

        return dict;
    }

    private static void AddThemedBrush(Dictionary<string, object> dict, ThemeResourceKey themedResourceKey, string? key = null)
    {
        key ??= "Themed" + themedResourceKey;
        var brush = Application.Current.TryFindResource(themedResourceKey);
        if (brush != null)
            dict[key] = brush;
    }

    private static void AddThemedBrushFromColorKey(Dictionary<string, object> dict, ThemeResourceKey themedResourceKey, string key)
    {
        var brush = ToBrushFromColorKey(themedResourceKey);
        if (brush != null)
            dict[key] = brush;
    }

    private static void AddThemedStyle(Dictionary<string, object> dict, object styleKey, string? key = null)
    {
        key ??= styleKey.ToString().Replace("StyleKey", "");
        if (Application.Current.FindResource(styleKey) is Style style)
            dict[key] = style;
    }

    private static Brush? ToBrushFromColorKey(ThemeResourceKey key)
    {
        try
        {
            var color = VSColorTheme.GetThemedColor(key);
            return new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "VS-COLORS");
            return null;
        }
    }
}