using System.Text.Json.Serialization;
using System.Windows.Media;

namespace YogaNotes.Models;

public static class DocumentTypography
{
    public const string FontFamilyName = "Times New Roman, Times, Liberation Serif";
    public const double DefaultFontSizePt = 12.0;
    public const double DefaultFontSizeDip = 16.0; // 12pt at 96 DPI
    public const double DefaultLineHeight = 22.0;

    public static readonly FontFamily DefaultFontFamily = new(FontFamilyName);

    public static double PtToDip(double pt) => pt * 96.0 / 72.0;
    public static double DipToPt(double dip) => dip * 72.0 / 96.0;
}

public enum PaperStyle
{
    Blank,
    Ruled,
    Grid
}

public class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1360;
    public double Height { get; set; } = 860;
    public bool IsMaximized { get; set; }
}

public class Notebook
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "My Notebook";
    public List<Section> Sections { get; set; } = new();
}

public class Section
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Quick Notes";
    public string ColorHex { get; set; } = "#7719AA";
    public List<PageItem> Pages { get; set; } = new();
}

public class PageItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Untitled Page";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class PageData
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled Page";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public PaperStyle PaperStyle { get; set; } = PaperStyle.Blank;
    public string DocumentXaml { get; set; } = string.Empty;
    public string DocumentPlainText { get; set; } = string.Empty;
    public string StrokeData { get; set; } = string.Empty;

    // Kept only to migrate pre-document bubble notes into DocumentPlainText on load.
    [JsonPropertyName("TextBoxes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyNoteTextBox>? LegacyTextBoxes { get; set; }
}

public class LegacyNoteTextBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = string.Empty;
}
