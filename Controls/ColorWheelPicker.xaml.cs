using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YogaNotes.Controls;

public partial class ColorWheelPicker : UserControl
{
    private const int WheelSize = 180;
    private double _hue = 275;
    private double _saturation = 0.85;
    private double _value = 1.0;
    private bool _isDraggingWheel = false;
    private bool _suppressHexTextChanged = false;

    private static readonly List<(string Name, Color Color)> AllNamedColors = BuildNamedColors();

    public event EventHandler<Color>? ColorSelected;

    public ColorWheelPicker()
    {
        InitializeComponent();
        RenderWheel();
        PopulateSearchResults(string.Empty);
    }

    public void SetCurrentColor(Color color)
    {
        RgbToHsv(color, out _hue, out _saturation, out _value);
        if (_value < 0.05) _value = 0.05;
        BrightnessSlider.Value = _value;
        RenderWheel();
        UpdateSelectorFromHsv();
        UpdatePreviewUI(color);
    }

    private void RenderWheel()
    {
        var bmp = new WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[WheelSize * WheelSize * 4];
        double radius = WheelSize / 2.0;

        for (int y = 0; y < WheelSize; y++)
        {
            for (int x = 0; x < WheelSize; x++)
            {
                double dx = x - radius + 0.5;
                double dy = y - radius + 0.5;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int idx = (y * WheelSize + x) * 4;

                if (dist <= radius)
                {
                    double sat = Math.Min(dist / radius, 1.0);
                    double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    if (angle < 0) angle += 360.0;

                    var c = HsvToRgb(angle, sat, _value);

                    // Soft anti-aliasing at outer edge
                    byte alpha = 255;
                    if (dist > radius - 1.5)
                    {
                        double edge = Math.Clamp((radius - dist) / 1.5, 0.0, 1.0);
                        alpha = (byte)(edge * 255);
                    }

                    pixels[idx + 0] = c.B;
                    pixels[idx + 1] = c.G;
                    pixels[idx + 2] = c.R;
                    pixels[idx + 3] = alpha;
                }
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, WheelSize * 4, 0);
        WheelImage.Source = bmp;
    }

    private void WheelImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingWheel = true;
        WheelImage.CaptureMouse();
        PickColorAtPoint(e.GetPosition(WheelImage));
    }

    private void WheelImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingWheel)
        {
            PickColorAtPoint(e.GetPosition(WheelImage));
        }
    }

    private void WheelImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingWheel)
        {
            _isDraggingWheel = false;
            WheelImage.ReleaseMouseCapture();
        }
    }

    private void PickColorAtPoint(Point pt)
    {
        double radius = WheelSize / 2.0;
        double dx = pt.X - radius;
        double dy = pt.Y - radius;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double clampedDist = Math.Min(dist, radius - 1);
        double sat = Math.Clamp(clampedDist / radius, 0.0, 1.0);
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;

        _hue = angle;
        _saturation = sat;

        UpdateSelectorFromHsv();
        var color = HsvToRgb(_hue, _saturation, _value);
        UpdatePreviewUI(color);
        ColorSelected?.Invoke(this, color);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _value = BrightnessSlider.Value;
        RenderWheel();
        var color = HsvToRgb(_hue, _saturation, _value);
        UpdatePreviewUI(color);
        ColorSelected?.Invoke(this, color);
    }

    private void SearchHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchHexBox.Text.Trim();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

        if (_suppressHexTextChanged) return;

        // Try direct hex parsing (e.g. #FF5733 or FF5733 or #F0A)
        if (TryParseHexColor(query, out var hexColor))
        {
            RgbToHsv(hexColor, out _hue, out _saturation, out _value);
            if (_value < 0.05) _value = 0.05;
            BrightnessSlider.Value = _value;
            RenderWheel();
            UpdateSelectorFromHsv();
            UpdatePreviewUI(hexColor);
            ColorSelected?.Invoke(this, hexColor);
        }

        PopulateSearchResults(query);
    }

    private void SearchHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var query = SearchHexBox.Text.Trim();
            if (TryParseHexColor(query, out var hexColor))
            {
                ColorSelected?.Invoke(this, hexColor);
            }
            else
            {
                var firstMatch = AllNamedColors.FirstOrDefault(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(firstMatch.Name))
                {
                    SelectNamedColor(firstMatch.Color);
                }
            }
        }
    }

    private void PopulateSearchResults(string query)
    {
        SearchResultsPanel.Children.Clear();

        IEnumerable<(string Name, Color Color)> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            // Curated popular quick swatches
            var defaults = new[]
            {
                "Black", "SlateGray", "Crimson", "OrangeRed", "Gold",
                "LimeGreen", "Teal", "DodgerBlue", "MediumPurple", "HotPink",
                "Coral", "Turquoise", "DeepSkyBlue", "DarkViolet"
            };
            matches = defaults
                .Select(n => AllNamedColors.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Where(c => !string.IsNullOrEmpty(c.Name));
        }
        else
        {
            var clean = query.TrimStart('#');
            matches = AllNamedColors
                .Where(c => c.Name.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                            $"#{c.Color.R:X2}{c.Color.G:X2}{c.Color.B:X2}".Contains(clean, StringComparison.OrdinalIgnoreCase))
                .Take(28);
        }

        foreach (var (name, color) in matches)
        {
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var btn = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2.5),
                Background = new SolidColorBrush(color),
                ToolTip = $"{name} ({hex})",
                Cursor = Cursors.Hand,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1)
            };

            btn.Click += (_, _) => SelectNamedColor(color);
            SearchResultsPanel.Children.Add(btn);
        }
    }

    private void SelectNamedColor(Color color)
    {
        _suppressHexTextChanged = true;
        SetCurrentColor(color);
        _suppressHexTextChanged = false;
        ColorSelected?.Invoke(this, color);
    }

    private void UpdateSelectorFromHsv()
    {
        double radius = WheelSize / 2.0;
        double angleRad = _hue * Math.PI / 180.0;
        double r = _saturation * (radius - 2);
        double x = radius + r * Math.Cos(angleRad) - (WheelSelector.Width / 2.0);
        double y = radius + r * Math.Sin(angleRad) - (WheelSelector.Height / 2.0);
        Canvas.SetLeft(WheelSelector, x);
        Canvas.SetTop(WheelSelector, y);
    }

    private void UpdatePreviewUI(Color color)
    {
        PreviewSwatch.Background = new SolidColorBrush(color);
        PreviewHexLabel.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseHexColor(string input, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var hex = input.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }
        if (hex.Length != 6) return false;

        try
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            color = Color.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<(string Name, Color Color)> BuildNamedColors()
    {
        return typeof(Colors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Color) && p.Name != "Transparent")
            .Select(p => (p.Name, (Color)p.GetValue(null)!))
            .OrderBy(c => c.Name)
            .ToList();
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;

        double r1 = 0, g1 = 0, b1 = 0;
        if (h < 60) { r1 = c; g1 = x; }
        else if (h < 120) { r1 = x; g1 = c; }
        else if (h < 180) { g1 = c; b1 = x; }
        else if (h < 240) { g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; b1 = c; }
        else { r1 = c; b1 = x; }

        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((r1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b1 + m) * 255), 0, 255));
    }

    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);

        if (h < 0) h += 360;
    }
}
