using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SunplusChannelStudio;

/// <summary>
/// Button content: a Segoe Fluent Icons glyph followed by text. The glyph takes the button's colour,
/// and in the Arabic layout it moves to the right of the text with the rest of the window.
/// </summary>
public sealed class IconLabel : StackPanel
{
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly TextBlock _icon = new() { FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_icon);
        Children.Add(_text);
        // A disabled button disables its content too; dim it the way the theme dims plain button text.
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.4;
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconLabel),
        new PropertyMetadata("", (d, e) => ((IconLabel)d)._icon.Text = (string)e.NewValue));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconLabel),
        new PropertyMetadata("", (d, e) => ((IconLabel)d)._text.Text = (string)e.NewValue));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
