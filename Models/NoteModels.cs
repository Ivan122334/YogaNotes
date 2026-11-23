namespace YogaNotes.Models;

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
    public PaperStyle PaperStyle { get; set; } = PaperStyle.Ruled;
    public string StrokeData { get; set; } = string.Empty; // Base64 ISF ink
    public List<NoteTextBox> TextBoxes { get; set; } = new();
}

public class NoteTextBox
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public double X { get; set; } = 50;
    public double Y { get; set; } = 50;
    public double Width { get; set; } = 250;
    public double Height { get; set; } = 100;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 14;
    public string ColorHex { get; set; } = "#222222";
}
