using System.Text.Json.Serialization;
using System.Windows.Media;

namespace YogaNotes.Models;

/// <summary>
/// Central typography configuration for all document text (typed and handwriting-converted).
/// </summary>
public static class DocumentTypography
{
    public const string FontFamilyName = "Times New Roman, Times, Liberation Serif";
    public const double DefaultFontSizePt = 12.0;
    public const double DefaultFontSizeDip = 16.0; // 12pt at 96 DPI (12 * 96 / 72)
    public const double DefaultLineHeight = 22.0;  // ~1.375x line spacing

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
    public string ColorHex { get; set; } = "#7719AA"; // OneNote signature purple
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

    /// <summary>
    /// Serialized WPF FlowDocument XAML containing the full Word-style document and formatting.
    /// </summary>
    public string DocumentXaml { get; set; } = string.Empty;

    /// <summary>
    /// Plain-text fallback / search preview of the document.
    /// </summary>
    public string DocumentPlainText { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded Ink Serialized Format (ISF) strokes layered on the page.
    /// </summary>
    public string StrokeData { get; set; } = string.Empty;

    /// <summary>
    /// Legacy floating text boxes from earlier versions, kept only for automatic one-time migration
    /// into DocumentXaml so no existing user notes are lost.
    /// </summary>
    [JsonPropertyName("TextBoxes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyNoteTextBox>? LegacyTextBoxes { get; set; }
}

/// <summary>
/// Legacy model used solely to migrate pre-refactor note bubbles into the unified document.
/// </summary>
public class LegacyNoteTextBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = string.Empty;
}
