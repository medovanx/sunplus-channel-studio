using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunplusChannelStudio;

/// <summary>Draws the drop position as a line across the grid while channels are dragged.</summary>
public sealed class InsertionAdorner : Adorner
{
    readonly Pen _pen;
    double? _y;

    public InsertionAdorner(UIElement grid) : base(grid)
    {
        IsHitTestVisible = false;
        // Theme brushes follow the system accent and cannot be frozen, so the pen stays live.
        var brush = Application.Current.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        _pen = new Pen(brush, 2.5);
    }

    public void Show(double? y)
    {
        if (_y == y) return;
        _y = y;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_y is not { } y) return;
        double width = AdornedElement.RenderSize.Width;
        dc.DrawLine(_pen, new Point(4, y), new Point(width - 4, y));
        dc.DrawEllipse(_pen.Brush, null, new Point(5, y), 4, 4);
    }
}
