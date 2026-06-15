using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace NetPeekier.App.Views;

/// <summary>
/// Modal tag picker: an editable combo box pre-filled with the existing tags
/// so you can either pick one or type a new name. Returns the chosen/typed
/// string, or null if cancelled.
/// </summary>
public sealed class TagPromptDialog : Window
{
    private readonly ComboBox _combo;
    private string? _result;

    private TagPromptDialog(Window owner, string current, IEnumerable<string> existingTags)
    {
        Owner = owner;
        Title = "Set tag";
        Width = 360;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Pick an existing tag or type a new one (processes sharing a tag can share a block or rule):",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _combo = new ComboBox { IsEditable = true, Text = current };
        foreach (var t in existingTags) _combo.Items.Add(t);
        root.Children.Add(_combo);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
        ok.Click += (_, _) => { _result = _combo.Text; DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _combo.Focus(); };
    }

    public static string? Ask(Window owner, string current) =>
        Ask(owner, current, System.Array.Empty<string>());

    public static string? Ask(Window owner, string current, IEnumerable<string> existingTags)
    {
        var dlg = new TagPromptDialog(owner, current, existingTags);
        return dlg.ShowDialog() == true ? dlg._result : null;
    }
}
