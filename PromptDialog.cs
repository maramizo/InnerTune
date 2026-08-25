using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InnerTune;

public sealed class PromptDialog : Window
{
    private readonly System.Windows.Controls.TextBox _input;
    private PromptDialog(Window owner, string title, string label, string initial)
    {
        Owner = owner; Title = title; Width = 420; Height = 190; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.ToolWindow; Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 34));
        Foreground = System.Windows.Media.Brushes.White; FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text,Segoe UI");
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.Children.Add(new TextBlock { Text = label, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) });
        _input = new System.Windows.Controls.TextBox { Text = initial, FontSize = 14 }; Grid.SetRow(_input, 1); root.Children.Add(_input);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Bottom };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new System.Windows.Controls.Button { Content = "Save", IsDefault = true, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(116, 89, 200)) };
        ok.Click += (_, _) => { DialogResult = true; Close(); }; buttons.Children.Add(cancel); buttons.Children.Add(ok); Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        Content = root; Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var dialog = new PromptDialog(owner, title, label, initial);
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}
