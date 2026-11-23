using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using YogaNotes.Services;

namespace YogaNotes.Tests;

public class ShapeRecognizerTests
{
    private static Stroke MakeStroke(IEnumerable<Point> pts, Color? color = null, double width = 4.0)
    {
        var spc = new StylusPointCollection();
        foreach (var p in pts)
            spc.Add(new StylusPoint(p.X, p.Y, 0.5f));

        var attrs = new DrawingAttributes
        {
            Color = color ?? Colors.DarkBlue,
            Width = width,
            Height = width
        };
        return new Stroke(spc, attrs);
    }

    private static List<Point> SamplePolyline(IList<Point> vertices, int stepsPerEdge = 15, double jitter = 0)
    {
        var rng = new Random(42);
        var pts = new List<Point>();
        for (int i = 0; i < vertices.Count - 1; i++)
        {
            var a = vertices[i];
            var b = vertices[i + 1];
            for (int s = 0; s < stepsPerEdge; s++)
            {
                double t = (double)s / stepsPerEdge;
                double jx = jitter > 0 ? (rng.NextDouble() - 0.5) * jitter : 0;
                double jy = jitter > 0 ? (rng.NextDouble() - 0.5) * jitter : 0;
                pts.Add(new Point(a.X + (b.X - a.X) * t + jx, a.Y + (b.Y - a.Y) * t + jy));
            }
        }
        pts.Add(vertices[^1]);
        return pts;
    }

    [Fact]
    public void RecognizesRoughCircle_AndPreservesStyle()
    {
        var rng = new Random(7);
        var pts = new List<Point>();
        for (int i = 0; i <= 60; i++)
        {
            double angle = 2 * Math.PI * i / 60.0;
            double r = 80 + (rng.NextDouble() - 0.5) * 6;
            pts.Add(new Point(200 + r * Math.Cos(angle), 200 + r * Math.Sin(angle)));
        }

        var stroke = MakeStroke(pts, Colors.Crimson, 6.0);
        var result = ShapeRecognizer.Analyze(stroke);

        Assert.True(result.IsRecognized);
        Assert.Equal(RecognizedShapeKind.Circle, result.Kind);
        Assert.Equal(Colors.Crimson, result.CleanedStroke!.DrawingAttributes.Color);
        Assert.Equal(6.0, result.CleanedStroke.DrawingAttributes.Width);
    }

    [Fact]
    public void RecognizesEllipse()
    {
        var pts = new List<Point>();
        for (int i = 0; i <= 60; i++)
        {
            double angle = 2 * Math.PI * i / 60.0;
            pts.Add(new Point(250 + 120 * Math.Cos(angle), 200 + 55 * Math.Sin(angle)));
        }

        var result = ShapeRecognizer.Analyze(MakeStroke(pts));
        Assert.Equal(RecognizedShapeKind.Ellipse, result.Kind);
    }

    [Fact]
    public void RecognizesStraightLine()
    {
        var pts = SamplePolyline(new[] { new Point(50, 100), new Point(300, 104) }, 25, jitter: 2.0);
        var result = ShapeRecognizer.Analyze(MakeStroke(pts));

        Assert.Equal(RecognizedShapeKind.Line, result.Kind);
        // Nearly horizontal should snap cleanly to horizontal
        Assert.Equal(result.CleanedStroke!.StylusPoints[0].Y, result.CleanedStroke.StylusPoints[^1].Y, precision: 2);
    }

    [Fact]
    public void RecognizesRectangle()
    {
        var corners = new[]
        {
            new Point(100, 100),
            new Point(320, 102),
            new Point(318, 220),
            new Point(98, 218),
            new Point(101, 102)
        };
        var pts = SamplePolyline(corners, 20, jitter: 2.5);
        var result = ShapeRecognizer.Analyze(MakeStroke(pts));

        Assert.Equal(RecognizedShapeKind.Rectangle, result.Kind);
    }

    [Fact]
    public void RecognizesSquare()
    {
        var corners = new[]
        {
            new Point(100, 100),
            new Point(220, 101),
            new Point(219, 221),
            new Point(100, 220),
            new Point(101, 101)
        };
        var pts = SamplePolyline(corners, 20, jitter: 2.0);
        var result = ShapeRecognizer.Analyze(MakeStroke(pts));

        Assert.Equal(RecognizedShapeKind.Square, result.Kind);
    }

    [Fact]
    public void RecognizesTriangle()
    {
        var corners = new[]
        {
            new Point(200, 80),
            new Point(300, 240),
            new Point(100, 240),
            new Point(202, 82)
        };
        var pts = SamplePolyline(corners, 20, jitter: 2.5);
        var result = ShapeRecognizer.Analyze(MakeStroke(pts));

        Assert.Equal(RecognizedShapeKind.Triangle, result.Kind);
    }

    [Fact]
    public void RecognizesDiamond()
    {
        var corners = new[]
        {
            new Point(200, 80),
            new Point(290, 170),
            new Point(200, 260),
            new Point(110, 170),
            new Point(201, 82)
        };
        var pts = SamplePolyline(corners, 20, jitter: 2.0);
        var result = ShapeRecognizer.Analyze(MakeStroke(pts));

        Assert.Equal(RecognizedShapeKind.Diamond, result.Kind);
    }

    [Fact]
    public void RecognizesSingleStrokeArrow()
    {
        var pts = SamplePolyline(new[]
        {
            new Point(60, 150),
            new Point(260, 150),
            new Point(225, 125),
            new Point(260, 150),
            new Point(225, 175)
        }, 16, jitter: 1.5);

        var result = ShapeRecognizer.Analyze(MakeStroke(pts));
        Assert.Equal(RecognizedShapeKind.Arrow, result.Kind);
    }

    [Fact]
    public void LeavesFreehandSquiggleUntouched()
    {
        // Simulates a cursive word / wavy freehand drawing
        var pts = new List<Point>();
        for (int i = 0; i <= 80; i++)
        {
            double x = 50 + i * 3.5;
            double y = 150 + Math.Sin(i * 0.45) * 35 + Math.Cos(i * 0.2) * 15;
            pts.Add(new Point(x, y));
        }

        var result = ShapeRecognizer.Analyze(MakeStroke(pts));
        Assert.False(result.IsRecognized);
        Assert.Equal(RecognizedShapeKind.None, result.Kind);
        Assert.Null(result.CleanedStroke);
    }
}
