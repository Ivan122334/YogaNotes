using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace YogaNotes.Services;

public enum RecognizedShapeKind
{
    None,
    Line,
    Arrow,
    Circle,
    Ellipse,
    Rectangle,
    Square,
    Triangle,
    Diamond
}

public readonly record struct ShapeRecognitionResult(Stroke? CleanedStroke, RecognizedShapeKind Kind)
{
    public bool IsRecognized => CleanedStroke != null && Kind != RecognizedShapeKind.None;
}

public static class ShapeRecognizer
{
    public static ShapeRecognitionResult Analyze(Stroke rawStroke)
    {
        var pts = rawStroke.StylusPoints.Select(p => new Point(p.X, p.Y)).ToList();
        if (pts.Count < 4) return default;

        var bounds = rawStroke.GetBounds();
        double diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        if (diagonal < 32) return default; // Ignore small marks and individual letters.

        double pathLength = ComputePathLength(pts);
        if (pathLength < 32) return default;

        double startEndDist = Distance(pts[0], pts[^1]);

        if (startEndDist / pathLength > 0.93)
        {
            var start = pts[0];
            var end = pts[^1];

            double dx = Math.Abs(end.X - start.X);
            double dy = Math.Abs(end.Y - start.Y);
            if (dy < dx * 0.085) end.Y = start.Y;
            else if (dx < dy * 0.085) end.X = start.X;

            var lineStroke = CreateStrokeFromPoints(InterpolateSegment(start, end, 40), rawStroke.DrawingAttributes);
            return new ShapeRecognitionResult(lineStroke, RecognizedShapeKind.Line);
        }

        var arrowResult = TryRecognizeArrow(pts, diagonal, rawStroke.DrawingAttributes);
        if (arrowResult.IsRecognized)
        {
            return arrowResult;
        }

        bool isClosed = startEndDist < Math.Max(diagonal * 0.28, 30);
        if (!isClosed) return default;

        // Reject multi-loop strokes like cursive words.
        if (pathLength / diagonal > 5.2) return default;

        var closedPts = new List<Point>(pts) { pts[0] };
        double epsilon = diagonal * 0.06;
        var simplified = RamerDouglasPeucker(closedPts, epsilon);
        if (simplified.Count > 1 && Distance(simplified[0], simplified[^1]) < epsilon * 1.4)
        {
            simplified.RemoveAt(simplified.Count - 1);
        }
        simplified = MergeCloseVertices(simplified, diagonal * 0.11);

        double cx = bounds.X + bounds.Width / 2.0;
        double cy = bounds.Y + bounds.Height / 2.0;
        double rx = Math.Max(bounds.Width / 2.0, 1.0);
        double ry = Math.Max(bounds.Height / 2.0, 1.0);

        var normalizedRadii = pts.Select(p =>
        {
            double nx = (p.X - cx) / rx;
            double ny = (p.Y - cy) / ry;
            return Math.Sqrt(nx * nx + ny * ny);
        }).ToList();

        double meanR = normalizedRadii.Average();
        double stdDev = Math.Sqrt(normalizedRadii.Average(r => (r - meanR) * (r - meanR)));

        double polyArea = Math.Abs(ComputePolygonArea(pts));
        double bboxArea = Math.Max(bounds.Width * bounds.Height, 1.0);
        double fillRatio = polyArea / bboxArea;

        if (simplified.Count == 3 && fillRatio >= 0.30 && fillRatio <= 0.68)
        {
            if (ComputeClosedPerimeter(simplified) / pathLength > 0.78)
            {
                return new ShapeRecognitionResult(
                    CreatePolygonStroke(simplified, rawStroke.DrawingAttributes),
                    RecognizedShapeKind.Triangle);
            }
        }

        if (simplified.Count == 4 && ComputeClosedPerimeter(simplified) / pathLength > 0.80)
        {
            if (IsDiamond(simplified, bounds))
            {
                var diamondCorners = new List<Point>
                {
                    new(cx, bounds.Top),
                    new(bounds.Right, cy),
                    new(cx, bounds.Bottom),
                    new(bounds.Left, cy)
                };
                return new ShapeRecognitionResult(
                    CreatePolygonStroke(diamondCorners, rawStroke.DrawingAttributes),
                    RecognizedShapeKind.Diamond);
            }

            if (HasRightAngles(simplified, 24))
            {
                if (IsAxisAlignedRectangle(simplified, bounds))
                {
                    double w = bounds.Width;
                    double h = bounds.Height;
                    double x = bounds.X;
                    double y = bounds.Y;

                    bool isSquare = Math.Abs(w - h) / Math.Max(w, h) < 0.16;
                    if (isSquare)
                    {
                        double side = (w + h) / 2.0;
                        x = cx - side / 2.0;
                        y = cy - side / 2.0;
                        w = side;
                        h = side;
                    }

                    var rectCorners = new List<Point>
                    {
                        new(x, y),
                        new(x + w, y),
                        new(x + w, y + h),
                        new(x, y + h)
                    };
                    return new ShapeRecognitionResult(
                        CreatePolygonStroke(rectCorners, rawStroke.DrawingAttributes),
                        isSquare ? RecognizedShapeKind.Square : RecognizedShapeKind.Rectangle);
                }

                var (rotatedCorners, isRotatedSquare) = BuildCleanRotatedRectangle(simplified);
                return new ShapeRecognitionResult(
                    CreatePolygonStroke(rotatedCorners, rawStroke.DrawingAttributes),
                    isRotatedSquare ? RecognizedShapeKind.Square : RecognizedShapeKind.Rectangle);
            }
        }

        if (stdDev < 0.13 && fillRatio >= 0.60 && fillRatio <= 0.88)
        {
            bool isCircle = Math.Abs(rx - ry) / Math.Max(rx, ry) < 0.18;
            if (isCircle)
            {
                double r = (rx + ry) / 2.0;
                rx = r;
                ry = r;
            }

            var ellipseStroke = CreateStrokeFromPoints(GenerateEllipsePoints(cx, cy, rx, ry, 72), rawStroke.DrawingAttributes);
            return new ShapeRecognitionResult(ellipseStroke, isCircle ? RecognizedShapeKind.Circle : RecognizedShapeKind.Ellipse);
        }

        return default;
    }

    private static ShapeRecognitionResult TryRecognizeArrow(List<Point> pts, double diagonal, DrawingAttributes attrs)
    {
        var simplified = RamerDouglasPeucker(pts, diagonal * 0.055);
        if (simplified.Count < 3 || simplified.Count > 5) return default;

        var tail = simplified[0];
        var tip = simplified[1];
        double shaftLen = Distance(tail, tip);
        if (shaftLen < diagonal * 0.55 || shaftLen < 40) return default;

        int tipIndex = FindClosestPointIndex(pts, tip);
        if (tipIndex < 2 || tipIndex >= pts.Count - 1) return default;

        double shaftPathLen = ComputePathLength(pts.Take(tipIndex + 1).ToList());
        if (shaftLen / Math.Max(shaftPathLen, 1.0) < 0.90) return default;

        var wingEnd = simplified[2];
        double wingLen = Distance(tip, wingEnd);
        if (wingLen < shaftLen * 0.10 || wingLen > shaftLen * 0.55) return default;

        Vector shaftBack = new(tail.X - tip.X, tail.Y - tip.Y);
        Vector wingVec = new(wingEnd.X - tip.X, wingEnd.Y - tip.Y);
        double angle = Math.Abs(Vector.AngleBetween(shaftBack, wingVec));
        if (angle < 12 || angle > 72) return default;

        for (int i = 2; i < simplified.Count; i++)
        {
            if (Distance(tip, simplified[i]) > shaftLen * 0.60)
                return default;
        }

        double headLen = Math.Clamp(shaftLen * 0.22, 14, 34);
        Vector dir = new(tail.X - tip.X, tail.Y - tip.Y);
        dir.Normalize();

        var leftWing = RotateVector(dir, 28) * headLen;
        var rightWing = RotateVector(dir, -28) * headLen;

        var arrowPts = new List<Point>();
        arrowPts.AddRange(InterpolateSegment(tail, tip, 36));
        arrowPts.AddRange(InterpolateSegment(tip, tip + leftWing, 12));
        arrowPts.AddRange(InterpolateSegment(tip + leftWing, tip, 12));
        arrowPts.AddRange(InterpolateSegment(tip, tip + rightWing, 12));

        return new ShapeRecognitionResult(CreateStrokeFromPoints(arrowPts, attrs), RecognizedShapeKind.Arrow);
    }

    private static bool IsDiamond(List<Point> corners, Rect bounds)
    {
        if (corners.Count != 4) return false;
        double cx = bounds.X + bounds.Width / 2.0;
        double cy = bounds.Y + bounds.Height / 2.0;
        double tol = Math.Max(bounds.Width, bounds.Height) * 0.22;

        var expectedMidpoints = new[]
        {
            new Point(cx, bounds.Top),
            new Point(bounds.Right, cy),
            new Point(cx, bounds.Bottom),
            new Point(bounds.Left, cy)
        };

        return expectedMidpoints.Count(exp => corners.Any(c => Distance(c, exp) < tol)) == 4;
    }

    private static bool HasRightAngles(List<Point> corners, double toleranceDegrees)
    {
        if (corners.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var prev = corners[(i + 3) % 4];
            var curr = corners[i];
            var next = corners[(i + 1) % 4];

            var v1 = prev - curr;
            var v2 = next - curr;
            if (v1.Length < 1e-3 || v2.Length < 1e-3) return false;

            if (Math.Abs(Math.Abs(Vector.AngleBetween(v1, v2)) - 90.0) > toleranceDegrees)
                return false;
        }
        return true;
    }

    private static bool IsAxisAlignedRectangle(List<Point> corners, Rect bounds)
    {
        double tol = Math.Max(bounds.Width, bounds.Height) * 0.18;
        var expected = new[]
        {
            new Point(bounds.Left, bounds.Top),
            new Point(bounds.Right, bounds.Top),
            new Point(bounds.Right, bounds.Bottom),
            new Point(bounds.Left, bounds.Bottom)
        };

        return expected.Count(exp => corners.Any(c => Distance(c, exp) < tol)) >= 3;
    }

    private static (List<Point> Corners, bool IsSquare) BuildCleanRotatedRectangle(List<Point> corners)
    {
        double cx = corners.Average(p => p.X);
        double cy = corners.Average(p => p.Y);
        var edge = corners[1] - corners[0];
        double angle = Math.Atan2(edge.Y, edge.X);
        double cos = Math.Cos(-angle);
        double sin = Math.Sin(-angle);

        var local = corners.Select(p =>
        {
            double dx = p.X - cx;
            double dy = p.Y - cy;
            return new Point(dx * cos - dy * sin, dx * sin + dy * cos);
        }).ToList();

        double minX = local.Min(p => p.X);
        double maxX = local.Max(p => p.X);
        double minY = local.Min(p => p.Y);
        double maxY = local.Max(p => p.Y);

        double w = maxX - minX;
        double h = maxY - minY;
        bool isSquare = Math.Abs(w - h) / Math.Max(w, h) < 0.16;
        if (isSquare)
        {
            double half = (w + h) / 4.0;
            double lcx = (minX + maxX) / 2.0;
            double lcy = (minY + maxY) / 2.0;
            minX = lcx - half; maxX = lcx + half;
            minY = lcy - half; maxY = lcy + half;
        }

        var cleanLocal = new[]
        {
            new Point(minX, minY),
            new Point(maxX, minY),
            new Point(maxX, maxY),
            new Point(minX, maxY)
        };

        double cosFwd = Math.Cos(angle);
        double sinFwd = Math.Sin(angle);
        var cleanWorld = cleanLocal.Select(p =>
            new Point(cx + p.X * cosFwd - p.Y * sinFwd, cy + p.X * sinFwd + p.Y * cosFwd)).ToList();

        return (cleanWorld, isSquare);
    }

    private static Vector RotateVector(Vector v, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    private static int FindClosestPointIndex(List<Point> pts, Point target)
    {
        int bestIdx = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double d = Distance(pts[i], target);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private static Stroke CreatePolygonStroke(List<Point> vertices, DrawingAttributes attrs)
    {
        var points = new List<Point>();
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            points.AddRange(InterpolateSegment(a, b, 24));
        }
        points.Add(vertices[0]);
        return CreateStrokeFromPoints(points, attrs);
    }

    private static List<Point> GenerateEllipsePoints(double cx, double cy, double rx, double ry, int segments)
    {
        var pts = new List<Point>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            pts.Add(new Point(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
        }
        return pts;
    }

    private static List<Point> InterpolateSegment(Point a, Point b, int steps)
    {
        var pts = new List<Point>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            pts.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
        }
        return pts;
    }

    private static Stroke CreateStrokeFromPoints(List<Point> points, DrawingAttributes attrs)
    {
        var spc = new StylusPointCollection();
        foreach (var p in points)
        {
            spc.Add(new StylusPoint(p.X, p.Y, 0.5f));
        }
        var cleanAttrs = attrs.Clone();
        cleanAttrs.FitToCurve = false;
        return new Stroke(spc, cleanAttrs);
    }

    private static List<Point> RamerDouglasPeucker(List<Point> points, double epsilon)
    {
        if (points.Count < 3) return new List<Point>(points);

        double maxDist = 0;
        int index = 0;
        int end = points.Count - 1;

        for (int i = 1; i < end; i++)
        {
            double d = PerpendicularDistance(points[i], points[0], points[end]);
            if (d > maxDist)
            {
                maxDist = d;
                index = i;
            }
        }

        if (maxDist > epsilon)
        {
            var left = RamerDouglasPeucker(points.Take(index + 1).ToList(), epsilon);
            var right = RamerDouglasPeucker(points.Skip(index).ToList(), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        return new List<Point> { points[0], points[end] };
    }

    private static List<Point> MergeCloseVertices(List<Point> vertices, double minDist)
    {
        if (vertices.Count <= 3) return vertices;
        var result = new List<Point> { vertices[0] };
        for (int i = 1; i < vertices.Count; i++)
        {
            if (Distance(result[^1], vertices[i]) >= minDist)
                result.Add(vertices[i]);
        }
        if (result.Count > 3 && Distance(result[0], result[^1]) < minDist)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static double PerpendicularDistance(Point pt, Point lineStart, Point lineEnd)
    {
        double dx = lineEnd.X - lineStart.X;
        double dy = lineEnd.Y - lineStart.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6) return Distance(pt, lineStart);

        double num = Math.Abs(dy * pt.X - dx * pt.Y + lineEnd.X * lineStart.Y - lineEnd.Y * lineStart.X);
        return num / Math.Sqrt(lenSq);
    }

    private static double ComputePathLength(List<Point> pts)
    {
        double total = 0;
        for (int i = 1; i < pts.Count; i++)
            total += Distance(pts[i - 1], pts[i]);
        return total;
    }

    private static double ComputeClosedPerimeter(List<Point> vertices)
    {
        double total = 0;
        for (int i = 0; i < vertices.Count; i++)
            total += Distance(vertices[i], vertices[(i + 1) % vertices.Count]);
        return total;
    }

    private static double ComputePolygonArea(List<Point> pts)
    {
        double area = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p1 = pts[i];
            var p2 = pts[(i + 1) % pts.Count];
            area += (p1.X * p2.Y - p2.X * p1.Y);
        }
        return area / 2.0;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
