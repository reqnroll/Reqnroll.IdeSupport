#nullable enable

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// VS-themed modal picker for <see cref="NavigationTarget"/> lists.
/// Displays choices in a vertical <see cref="ListBox"/> sized to its content.
/// Used by <see cref="NavigationPickerHelper"/> when a query returns multiple results.
/// </summary>
internal sealed class NavigationPickerDialog : DialogWindow
{
    private readonly ListBox _listBox;

    /// <summary>Index of the selected item in the list, or -1 if nothing is selected.</summary>
    public int SelectedIndex => _listBox.SelectedIndex;

    /// <summary>Creates the dialog with the given title and choices, selecting the first item by default.</summary>
    public NavigationPickerDialog(string title, IReadOnlyList<NavigationTarget> targets)
    {
        Title                 = title;
        HasMinimizeButton     = false;
        HasMaximizeButton     = false;
        ResizeMode            = ResizeMode.CanResizeWithGrip;
        SizeToContent         = SizeToContent.WidthAndHeight;
        MinWidth              = 480;
        MaxWidth              = 900;
        MaxHeight             = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _listBox = BuildListBox(targets);
        Content  = BuildLayout(_listBox);
    }

    // ── Content builders ──────────────────────────────────────────────────────

    private static ListBox BuildListBox(IReadOnlyList<NavigationTarget> targets)
    {
        var lb = new ListBox
        {
            Margin        = new Thickness(8, 8, 8, 4),
            SelectionMode = SelectionMode.Single,
        };

        // Apply VS theme colours so the list matches the current VS colour scheme.
        lb.SetResourceReference(Control.ForegroundProperty,   VsBrushes.WindowTextKey);
        lb.SetResourceReference(Control.BackgroundProperty,   VsBrushes.WindowKey);
        lb.SetResourceReference(Control.BorderBrushProperty,  VsBrushes.ActiveBorderKey);

        foreach (var t in targets)
            lb.Items.Add(new ListBoxItem { Content = t.DisplayText, Padding = new Thickness(4, 2, 4, 2) });

        if (lb.Items.Count > 0)
            lb.SelectedIndex = 0;

        return lb;
    }

    private StackPanel BuildLayout(ListBox listBox)
    {
        var goButton = new Button
        {
            Content   = "Go",
            IsDefault = true,
            MinWidth  = 75,
            Margin    = new Thickness(8, 4, 8, 8),
        };
        // IsCancel gives both the button and the Esc key a dismiss path: pressing Esc
        // (or clicking Cancel) sets DialogResult to false and closes the window.
        var cancelButton = new Button
        {
            Content   = "Cancel",
            IsCancel  = true,
            MinWidth  = 75,
            Margin    = new Thickness(8, 4, 0, 8),
        };
        goButton.Click           += (_, _) => Accept();
        listBox.MouseDoubleClick += (_, _) => Accept();
        listBox.KeyDown          += OnListKeyDown;

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(goButton);

        var root = new StackPanel();
        root.Children.Add(listBox);
        root.Children.Add(buttons);
        return root;
    }

    // ── Input handling ────────────────────────────────────────────────────────

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
            Accept();
    }

    private void Accept()
    {
        if (_listBox.SelectedIndex >= 0)
        {
            DialogResult = true;
            Close();
        }
    }
}
