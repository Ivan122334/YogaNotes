using System.Windows;
using System.Windows.Media;
using YogaNotes.Models;

namespace YogaNotes.Services;

public static class PaperBackgroundHelper
{
    public static Brush GetPaperBrush(PaperStyle style)
    {
        return style switch
        {
            PaperStyle.Ruled => CreateRuledBrush(),
            PaperStyle.Grid => CreateGridBrush(),
            PaperStyle.Blank => CreateBlankBrush(),
            _ => CreateBlankBrush()
        };
    }

    public static Brush CreateRuledBrush(double lineSpacing = 32)
    {
        var group = new DrawingGroup();

        // White paper background
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(254, 254, 254)),
            null,
            new RectangleGeometry(new Rect(0, 0, 100, lineSpacing))));

        // Horizontal ruled line
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
        linePen.Freeze();

        group.Children.Add(new GeometryDrawing(
            null,
            linePen,
            new LineGeometry(new Point(0, lineSpacing), new Point(100, lineSpacing))));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 100, lineSpacing),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };

        brush.Freeze();
        return brush;
    }

    public static Brush CreateGridBrush(double gridSize = 28)
    {
        var group = new DrawingGroup();

        // White paper background
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(254, 254, 254)),
            null,
            new RectangleGeometry(new Rect(0, 0, gridSize, gridSize))));

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
        gridPen.Freeze();

        // Horizontal and vertical grid lines
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, gridSize), new Point(gridSize, gridSize)));
        geometry.Children.Add(new LineGeometry(new Point(gridSize, 0), new Point(gridSize, gridSize)));

        group.Children.Add(new GeometryDrawing(null, gridPen, geometry));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, gridSize, gridSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };

        brush.Freeze();
        return brush;
    }

    public static Brush CreateBlankBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(254, 254, 254));
        brush.Freeze();
        return brush;
    }
}
