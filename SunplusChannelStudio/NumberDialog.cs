using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SunplusChannelStudio;

/// <summary>A small modal prompt for a whole number in a range.</summary>
public sealed class NumberDialog : Window
{
    readonly TextBox _box;
    readonly TextBlock _error;
    readonly int _min, _max;
    int? _value;

    NumberDialog(string title, string prompt, int initial, int min, int max)
    {
        _min = min;
        _max = max;
        Title = title;
        FlowDirection = Loc.Flow;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _box = new TextBox { Text = initial.ToString(), Width = 320, Margin = new Thickness(0, 10, 0, 0) };
        _error = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush"),
        };
        var ok = new Button { Content = Loc.Get("Dlg.MoveButton"), IsDefault = true, Padding = new Thickness(18, 5, 18, 5), MinWidth = 90 };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        var cancel = new Button { Content = Loc.Get("Dlg.Cancel"), IsCancel = true, Padding = new Thickness(18, 5, 18, 5), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Accept();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        _box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
    }

    void Accept()
    {
        if (int.TryParse(_box.Text, out int n) && n >= _min && n <= _max)
        {
            _value = n;
            DialogResult = true;
            return;
        }
        _error.Text = Loc.Format("Dlg.NumberRange", _min, _max);
        _error.Visibility = Visibility.Visible;
        _box.Focus();
        _box.SelectAll();
    }

    public static int? Ask(Window owner, string title, string prompt, int initial, int min, int max)
    {
        var dialog = new NumberDialog(title, prompt, initial, min, max) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._value : null;
    }
}
