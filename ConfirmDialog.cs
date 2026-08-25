using System.Windows;
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;

namespace InnerTune;

public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(Window owner, string title, string heading, string detail, string confirmLabel)
    {
        Owner = owner;
        Title = title;
        Width = 450;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(25, 25, 34));
        Foreground = WpfMedia.Brushes.White;
        FontFamily = new WpfMedia.FontFamily("Segoe UI Variable Text,Segoe UI");
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (AppRuntime.IsTestMode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left + 120;
            Top = owner.Top + 120;
            ShowActivated = false;
        }

        var root = new WpfControls.Grid { Margin = new Thickness(24, 21, 24, 20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.Children.Add(new WpfControls.TextBlock { Text = heading, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detailText = new WpfControls.TextBlock
        {
            Text = detail,
            Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(174, 171, 183)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        WpfControls.Grid.SetRow(detailText, 1);
        root.Children.Add(detailText);

        var buttons = new WpfControls.StackPanel
        {
            Orientation = WpfControls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom
        };
        var cancel = new WpfControls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var confirm = new WpfControls.Button
        {
            Content = confirmLabel,
            IsDefault = true,
            Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(116, 89, 200)),
            Padding = new Thickness(17, 6, 17, 6)
        };
        confirm.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        WpfControls.Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }

    public static bool Show(Window owner, string title, string heading, string detail, string confirmLabel) =>
        new ConfirmDialog(owner, title, heading, detail, confirmLabel).ShowDialog() == true;
}
