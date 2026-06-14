using System.Windows;
using System.Windows.Controls;

namespace NetPeekier.App.Views;

/// <summary>
/// Minimal modal text-input dialog for setting a process tag. WPF has no
/// built-in InputBox, so this is the small equivalent of the Python build's
/// ask_tag prompt. Returns the entered string, or null if cancelled.
/// </summary>
public sealed class TagPromptDialog : Window
{
    private readonly TextBox _box;
    private string? _result;

    private TagPromptDialog(Window owner, string current)
    {
        Owner = owner;
        Title = "Set tag";
        Width = 360;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Group tag (processes sharing a tag can share a block or speed limit):",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _box = new TextBox { Text = current };
        _box.SelectAll();
        root.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
        ok.Click += (_, _) => { _result = _box.Text; DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _box.Focus();
    }

    public static string? Ask(Window owner, string current)
    {
        var dlg = new TagPromptDialog(owner, current);
        return dlg.ShowDialog() == true ? dlg._result : null;
    }
}
