using System.Reflection;
using System.Windows;

namespace NetPeekier.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        VersionText.Text = $"Version {v}  ·  .NET 8";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
