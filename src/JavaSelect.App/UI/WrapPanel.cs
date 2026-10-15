using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
namespace JavaSelect.App.UI;
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size size)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;   // a hidden child must not leave its spacing behind
            child.Measure(new Size(size.Width, double.PositiveInfinity)); var d = child.DesiredSize;
            if (x > 0 && x + d.Width > size.Width) { width = Math.Max(width, x - Spacing); x = 0; y += row + Spacing; row = 0; }
            x += d.Width + Spacing; row = Math.Max(row, d.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - Spacing)), y + row);
    }
    protected override Size ArrangeOverride(Size size)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > size.Width) { x = 0; y += row + Spacing; row = 0; }
            child.Arrange(new Rect(x, y, Math.Min(d.Width, size.Width), d.Height)); x += d.Width + Spacing; row = Math.Max(row, d.Height);
        }
        return size;
    }
}
