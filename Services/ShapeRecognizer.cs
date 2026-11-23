using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace YogaNotes.Services;

public static class ShapeRecognizer
{
    public static Stroke? TryRecognizeAndCleanShape(Stroke rawStroke)
    {
        var pts = rawStroke.StylusPoints.Select(p => new Point(p.X, p.Y)).ToList();
        if (pts.Count < 4) return null;

        var bounds = rawStroke.GetBounds();
        double diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        if (diagonal < 20) return null; // Too tiny (e.g. a dot)

        double pathLength = ComputePathLength(pts);
        if (pathLength < 20) return null;

        double startEndDist = Distance(pts[0], pts[^1]);

        // 1. Check for Straight Line
        if (startEndDist / pathLength > 0.90)
        {
            var start = pts[0];
            var end = pts[^1];

            // Snap nearly horizontal or vertical lines (within ~6 degrees)
            double dx = Math.Abs(end.X - start.X);
            double dy = Math.Abs(end.Y - start.Y);
            if (dy < dx * 0.10) end.Y = start.Y;
            else if (dx < dy * 0.10) end.X = start.X;

            return CreateStrokeFromPoints(InterpolateSegment(start, end, 40), rawStroke.DrawingAttributes);
        }

        // 2. Check for Closed Shape (start and end reasonably close)
        bool isClosed = startEndDist < Math.Max(diagonal * 0.32, 35);
        if (!isClosed) return null;

        // 3. Check for Circle / Ellipse
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
        double variance = normalizedRadii.Average(r => (r - meanR) * (r - meanR));
        double stdDev = Math.Sqrt(variance);

        // Simplify polygon using Ramer-Douglas-Peucker
        var closedPts = new List<Point>(pts) { pts[0] };
        double epsilon = diagonal * 0.065;
        var simplified = RamerDouglasPeucker(closedPts, epsilon);
        // Remove duplicated closing vertex
        if (simplified.Count > 1 && Distance(simplified[0], simplified[^1]) < epsilon)
        {
            simplified.RemoveAt(simplified.Count - 1);
        }

        // Merge any corners that are too close together
        simplified = MergeCloseVertices(simplified, diagonal * 0.12);

        // Distinguish Circle/Ellipse vs Triangle vs Rectangle
        // A clean ellipse has very low normalized radial stdDev (< 0.125) and typically > 4 simplified vertices
        bool looksCircular = stdDev < 0.135 && simplified.Count != 3;

        // Also check rectangularity (area of stroke polygon vs bounding box area)
        double polyArea = Math.Abs(ComputePolygonArea(pts));
        double bboxArea = bounds.Width * bounds.Height;
        double fillRatio = bboxArea > 0 ? polyArea / bboxArea : 0;

        // Circle fill ratio is ~pi/4 (~0.785), rectangle is ~0.90-1.0, triangle is ~0.50
        if (simplified.Count == 3 && fillRatio < 0.68)
        {
            return CreatePolygonStroke(simplified, rawStroke.DrawingAttributes);
        }

        if (simplified.Count == 4 && (!looksCircular || fillRatio > 0.84))
        {
            // Check if it's roughly axis-aligned rectangle/square
            if (IsAxisAlignedRectangle(simplified, bounds))
            {
                double w = bounds.Width;
                double h = bounds.Height;
                double x = bounds.X;
                double y = bounds.Y;

                // Snap to square if nearly equal
                if (Math.Abs(w - h) / Math.Max(w, h) < 0.18)
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
                return CreatePolygonStroke(rectCorners, rawStroke.DrawingAttributes);
            }

            return CreatePolygonStroke(simplified, rawStroke.DrawingAttributes);
        }

        if (looksCircular || stdDev < 0.16)
        {
            // Snap to perfect circle if width and height are within 18%
            if (Math.Abs(rx - ry) / Math.Max(rx, ry) < 0.18)
            {
                double r = (rx + ry) / 2.0;
                rx = r;
                ry = r;
            }

            var ellipsePts = GenerateEllipsePoints(cx, cy, rx, ry, 72);
            return CreateStrokeFromPoints(ellipsePts, rawStroke.DrawingAttributes);
        }

        // Fallback for 3-6 corner clean polygons
        if (simplified.Count >= 3 && simplified.Count <= 6)
        {
            return CreatePolygonStroke(simplified, rawStroke.DrawingAttributes);
        }

        return null;
    }

    private static bool IsAxisAlignedRectangle(List<Point> corners, Rect bounds)
    {
        double tol = Math.Max(bounds.Width, bounds.Height) * 0.22;
        var expected = new[]
        {
            new Point(bounds.Left, bounds.Top),
            new Point(bounds.Right, bounds.Top),
            new Point(bounds.Right, bounds.Bottom),
            new Point(bounds.Left, bounds.Bottom)
        };

        int matched = 0;
        foreach (var exp in expected)
        {
            if (corners.Any(c => Distance(c, exp) < tol))
                matched++;
        }
        return matched >= 3;
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
        cleanAttrs.FitToCurve = false; // Keep geometric corners crisp!
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
            {
                result.Add(vertices[i]);
            }
        }
        if (result.Count > 3 && Distance(result[0], result[^1]) < minDist)
        {
            result.RemoveAt(result.Count - 1);
        }
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
