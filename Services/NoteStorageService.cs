using System.IO;
using System.Text.Json;
using System.Windows.Ink;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using YogaNotes.Models;

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
        // Store notes in User Documents/YogaNotesData for easy access and backup
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

        // Create default starter notebook
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
            PaperStyle = PaperStyle.Ruled
        };
    }

    public void SavePage(PageData pageData)
    {
        try
        {
            pageData.UpdatedAt = DateTime.Now;
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
        var width = (int)Math.Max(visual.ActualWidth, 1200);
        var height = (int)Math.Max(visual.ActualHeight, 1600);

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
            Title = "Welcome to YogaNotes!",
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

        // Pre-populate welcome page content with nice instructions
        var initialWelcomeData = new PageData
        {
            Id = welcomePage.Id,
            Title = welcomePage.Title,
            CreatedAt = welcomePage.CreatedAt,
            UpdatedAt = DateTime.Now,
            PaperStyle = PaperStyle.Ruled,
            TextBoxes = new List<NoteTextBox>
            {
                new()
                {
                    X = 60,
                    Y = 60,
                    Width = 650,
                    Height = 220,
                    Text = "Welcome to YogaNotes for Lenovo Yoga! ✍️\n\n" +
                           "• Freeform Canvas: Select the 'Text' tool or double click anywhere on the page to type notes.\n" +
                           "• Stylus Ready: Switch to 'Pen' or 'Highlighter' to sketch with true pressure sensitivity and palm rejection.\n" +
                           "• Lenovo Pen Support: Use your pen's eraser tip or hold the eraser button to erase ink seamlessly.\n" +
                           "• Paper Backgrounds: Easily switch between Ruled (lined), Grid, and Blank paper.\n" +
                           "• Hierarchy: Organize by Notebooks, Sections, and Pages on the left sidebar just like Microsoft OneNote."
                }
            }
        };

        SavePage(initialWelcomeData);
        return notebooks;
    }
}
