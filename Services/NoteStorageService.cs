using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YogaNotes.Models;
using Section = YogaNotes.Models.Section;

namespace YogaNotes.Services;

public class NoteStorageService
{
    private readonly string _baseDirectory;
    private readonly string _notebooksMetaFile;
    private readonly string _pagesDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public NoteStorageService()
    {
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _baseDirectory = Path.Combine(myDocuments, "YogaNotesData");
        _pagesDirectory = Path.Combine(_baseDirectory, "Pages");
        _notebooksMetaFile = Path.Combine(_baseDirectory, "notebooks.json");

        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_pagesDirectory);
    }

    public List<Notebook> LoadNotebooks()
    {
        try
        {
            if (File.Exists(_notebooksMetaFile))
            {
                var json = File.ReadAllText(_notebooksMetaFile);
                var notebooks = JsonSerializer.Deserialize<List<Notebook>>(json, JsonOptions);
                if (notebooks != null && notebooks.Count > 0)
                {
                    return notebooks;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load notebooks: {ex.Message}");
        }

        return CreateInitialData();
    }

    public void SaveNotebooks(List<Notebook> notebooks)
    {
        try
        {
            var json = JsonSerializer.Serialize(notebooks, JsonOptions);
            File.WriteAllText(_notebooksMetaFile, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save notebooks: {ex.Message}");
        }
    }

    public PageData LoadPage(string pageId, string fallbackTitle = "Untitled Page")
    {
        var filePath = GetPageFilePath(pageId);
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<PageData>(json, JsonOptions);
                if (data != null)
                {
                    // One-time migration from legacy floating note bubbles to unified Word document
                    if (string.IsNullOrWhiteSpace(data.DocumentXaml) &&
                        data.LegacyTextBoxes != null &&
                        data.LegacyTextBoxes.Count > 0)
                    {
                        var mergedText = string.Join(
                            "\n\n",
                            data.LegacyTextBoxes
                                .OrderBy(b => b.Y)
                                .ThenBy(b => b.X)
                                .Select(b => b.Text?.Trim())
                                .Where(t => !string.IsNullOrEmpty(t)));

                        data.DocumentPlainText = mergedText;
                        data.LegacyTextBoxes = null;
                        SavePage(data);
                    }

                    return data;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading page {pageId}: {ex.Message}");
            }
        }

        return new PageData
        {
            Id = pageId,
            Title = fallbackTitle,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            PaperStyle = PaperStyle.Blank
        };
    }

    public void SavePage(PageData pageData)
    {
        try
        {
            pageData.UpdatedAt = DateTime.Now;
            pageData.LegacyTextBoxes = null; // Never re-write legacy bubble format
            var filePath = GetPageFilePath(pageData.Id);
            var json = JsonSerializer.Serialize(pageData, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving page {pageData.Id}: {ex.Message}");
        }
    }

    public void DeletePageFile(string pageId)
    {
        var filePath = GetPageFilePath(pageId);
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch { }
        }
    }

    private string GetPageFilePath(string pageId)
    {
        return Path.Combine(_pagesDirectory, $"{pageId}.json");
    }

    /// <summary>
    /// Creates a clean FlowDocument configured with the central Times New Roman 12pt typography.
    /// </summary>
    public static FlowDocument CreateStyledDocument(string initialPlainText = "")
    {
        var doc = new FlowDocument
        {
            FontFamily = DocumentTypography.DefaultFontFamily,
            FontSize = DocumentTypography.DefaultFontSizeDip,
            LineHeight = DocumentTypography.DefaultLineHeight,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            PagePadding = new Thickness(0)
        };

        if (!string.IsNullOrEmpty(initialPlainText))
        {
            var lines = initialPlainText.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var p = new Paragraph(new Run(line))
                {
                    FontFamily = DocumentTypography.DefaultFontFamily,
                    FontSize = DocumentTypography.DefaultFontSizeDip,
                    LineHeight = DocumentTypography.DefaultLineHeight,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                doc.Blocks.Add(p);
            }
        }
        else
        {
            doc.Blocks.Add(new Paragraph
            {
                FontFamily = DocumentTypography.DefaultFontFamily,
                FontSize = DocumentTypography.DefaultFontSizeDip,
                LineHeight = DocumentTypography.DefaultLineHeight,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        return doc;
    }

    public static (string Xaml, string PlainText) SerializeFlowDocument(FlowDocument document)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        string plainText = range.Text.TrimEnd('\r', '\n');

        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        string xaml = Encoding.UTF8.GetString(ms.ToArray());

        return (xaml, plainText);
    }

    public static FlowDocument DeserializeFlowDocument(string xaml, string plainTextFallback)
    {
        var doc = CreateStyledDocument();

        if (!string.IsNullOrWhiteSpace(xaml))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(xaml);
                using var ms = new MemoryStream(bytes);
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                range.Load(ms, DataFormats.Xaml);

                // Ensure document-level defaults remain Times New Roman
                doc.FontFamily = DocumentTypography.DefaultFontFamily;
                doc.FontSize = DocumentTypography.DefaultFontSizeDip;
                doc.LineHeight = DocumentTypography.DefaultLineHeight;
                return doc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize XAML document, falling back to plain text: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(plainTextFallback))
        {
            return CreateStyledDocument(plainTextFallback);
        }

        return doc;
    }

    public static string SerializeStrokes(StrokeCollection strokes)
    {
        if (strokes == null || strokes.Count == 0) return string.Empty;
        using var ms = new MemoryStream();
        strokes.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static StrokeCollection DeserializeStrokes(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return new StrokeCollection();
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            return new StrokeCollection(ms);
        }
        catch
        {
            return new StrokeCollection();
        }
    }

    public static void ExportToPng(FrameworkElement visual, string filePath)
    {
        var width = (int)Math.Max(visual.ActualWidth, 850);
        var height = (int)Math.Max(visual.ActualHeight, 1120);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var fileStream = File.Create(filePath);
        encoder.Save(fileStream);
    }

    private List<Notebook> CreateInitialData()
    {
        var welcomePage = new PageItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Welcome to YogaNotes",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var quickNotes = new Section
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Quick Notes",
            ColorHex = "#7719AA",
            Pages = new List<PageItem> { welcomePage }
        };

        var ideasSection = new Section
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Ideas & Sketches",
            ColorHex = "#0EA5E9",
            Pages = new List<PageItem>
            {
                new() { Title = "Project Ideas", CreatedAt = DateTime.Now }
            }
        };

        var defaultNotebook = new Notebook
        {
            Id = Guid.NewGuid().ToString(),
            Name = "My Notebook",
            Sections = new List<Section> { quickNotes, ideasSection }
        };

        var notebooks = new List<Notebook> { defaultNotebook };
        SaveNotebooks(notebooks);

        string welcomeText =
            "Welcome to YogaNotes for Lenovo Yoga.\n\n" +
            "• Word-Style Document: Click anywhere on this page to type in Times New Roman with bold, italic, underline, alignment, and lists.\n" +
            "• Smart Drawing & Shape Assist: Switch to Draw mode to sketch with pressure sensitivity. Rough circles, rectangles, squares, triangles, straight lines, and arrows automatically snap into crisp shapes (press Ctrl+Z once to revert to your raw stroke).\n" +
            "• Inline Handwriting-to-Text: Use Write→Text in Draw mode to write in cursive English or Bulgarian — your handwriting converts directly into editable Times New Roman text inside this document.";

        var initialWelcomeData = new PageData
        {
            Id = welcomePage.Id,
            Title = welcomePage.Title,
            CreatedAt = welcomePage.CreatedAt,
            UpdatedAt = DateTime.Now,
            PaperStyle = PaperStyle.Blank,
            DocumentPlainText = welcomeText
        };

        SavePage(initialWelcomeData);
        return notebooks;
    }
}
