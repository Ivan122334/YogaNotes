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
    private readonly string _windowSettingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public NoteStorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _baseDirectory = Path.Combine(appData, "YogaNotes");
        _pagesDirectory = Path.Combine(_baseDirectory, "Pages");
        _notebooksMetaFile = Path.Combine(_baseDirectory, "notebooks.json");
        _windowSettingsFile = Path.Combine(_baseDirectory, "window.json");

        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_pagesDirectory);

        MigrateFromDocumentsIfNeeded();
    }

    private void MigrateFromDocumentsIfNeeded()
    {
        if (File.Exists(_notebooksMetaFile)) return;

        var oldDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "YogaNotesData");
        var oldMeta = Path.Combine(oldDir, "notebooks.json");
        if (!File.Exists(oldMeta)) return;

        try
        {
            File.Copy(oldMeta, _notebooksMetaFile, overwrite: false);
            var oldPagesDir = Path.Combine(oldDir, "Pages");
            if (Directory.Exists(oldPagesDir))
            {
                foreach (var file in Directory.GetFiles(oldPagesDir, "*.json"))
                {
                    var dest = Path.Combine(_pagesDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest);
                    }
                }
            }
        }
        catch
        {
            // Ignore partial migration errors and fall back to normal loading.
        }
    }

    public WindowSettings LoadWindowSettings()
    {
        if (!File.Exists(_windowSettingsFile)) return new WindowSettings();

        try
        {
            var json = File.ReadAllText(_windowSettingsFile);
            return JsonSerializer.Deserialize<WindowSettings>(json, JsonOptions) ?? new WindowSettings();
        }
        catch
        {
            return new WindowSettings();
        }
    }

    public void SaveWindowSettings(WindowSettings settings)
    {
        try
        {
            File.WriteAllText(_windowSettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { }
    }

    public List<Notebook> LoadNotebooks()
    {
        if (File.Exists(_notebooksMetaFile))
        {
            try
            {
                var json = File.ReadAllText(_notebooksMetaFile);
                var notebooks = JsonSerializer.Deserialize<List<Notebook>>(json, JsonOptions);
                if (notebooks != null && notebooks.Count > 0)
                {
                    return notebooks;
                }
            }
            catch { }
        }

        return CreateInitialData();
    }

    public void SaveNotebooks(List<Notebook> notebooks)
    {
        try
        {
            File.WriteAllText(_notebooksMetaFile, JsonSerializer.Serialize(notebooks, JsonOptions));
        }
        catch { }
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
                    if (string.IsNullOrWhiteSpace(data.DocumentXaml) &&
                        data.LegacyTextBoxes != null &&
                        data.LegacyTextBoxes.Count > 0)
                    {
                        data.DocumentPlainText = string.Join(
                            "\n\n",
                            data.LegacyTextBoxes
                                .OrderBy(b => b.Y)
                                .ThenBy(b => b.X)
                                .Select(b => b.Text?.Trim())
                                .Where(t => !string.IsNullOrEmpty(t)));
                        data.LegacyTextBoxes = null;
                        SavePage(data);
                    }

                    return data;
                }
            }
            catch { }
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
            pageData.LegacyTextBoxes = null;
            File.WriteAllText(GetPageFilePath(pageData.Id), JsonSerializer.Serialize(pageData, JsonOptions));
        }
        catch { }
    }

    public void DeletePageFile(string pageId)
    {
        var filePath = GetPageFilePath(pageId);
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); } catch { }
        }
    }

    private string GetPageFilePath(string pageId) => Path.Combine(_pagesDirectory, $"{pageId}.json");

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

        var lines = string.IsNullOrEmpty(initialPlainText)
            ? new[] { string.Empty }
            : initialPlainText.Replace("\r\n", "\n").Split('\n');

        foreach (var line in lines)
        {
            doc.Blocks.Add(new Paragraph(new Run(line))
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
        return (Encoding.UTF8.GetString(ms.ToArray()), plainText);
    }

    public static FlowDocument DeserializeFlowDocument(string xaml, string plainTextFallback)
    {
        var doc = CreateStyledDocument();

        if (!string.IsNullOrWhiteSpace(xaml))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                range.Load(ms, DataFormats.Xaml);

                doc.FontFamily = DocumentTypography.DefaultFontFamily;
                doc.FontSize = DocumentTypography.DefaultFontSizeDip;
                doc.LineHeight = DocumentTypography.DefaultLineHeight;
                return doc;
            }
            catch { }
        }

        return !string.IsNullOrEmpty(plainTextFallback)
            ? CreateStyledDocument(plainTextFallback)
            : doc;
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
            using var ms = new MemoryStream(Convert.FromBase64String(base64));
            return new StrokeCollection(ms);
        }
        catch
        {
            return new StrokeCollection();
        }
    }

    public static void ExportToPng(FrameworkElement visual, string filePath)
    {
        int width = (int)Math.Max(visual.ActualWidth, 850);
        int height = (int)Math.Max(visual.ActualHeight, 1120);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var fileStream = File.Create(filePath);
        encoder.Save(fileStream);
    }

    private List<Notebook> CreateInitialData()
    {
        var firstPage = new PageItem
        {
            Title = "Untitled Page",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var defaultNotebook = new Notebook
        {
            Name = "My Notebook",
            Sections = new List<Section>
            {
                new()
                {
                    Name = "Quick Notes",
                    ColorHex = "#7719AA",
                    Pages = new List<PageItem> { firstPage }
                }
            }
        };

        var notebooks = new List<Notebook> { defaultNotebook };
        SaveNotebooks(notebooks);
        SavePage(new PageData
        {
            Id = firstPage.Id,
            Title = firstPage.Title,
            CreatedAt = firstPage.CreatedAt,
            UpdatedAt = DateTime.Now,
            PaperStyle = PaperStyle.Blank
        });

        return notebooks;
    }
}
