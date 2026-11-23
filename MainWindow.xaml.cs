using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using YogaNotes.Controls;
using YogaNotes.Models;
using YogaNotes.Services;

namespace YogaNotes;

public partial class MainWindow : Window
{
    private readonly NoteStorageService _storageService;
    private List<Notebook> _notebooks = new();
    private Notebook? _currentNotebook;
    private Section? _currentSection;
    private PageItem? _currentPageItem;
    private PageData? _currentPageData;

    // Ink undo / redo history
    private readonly Stack<StrokeCollection> _undoStack = new();
    private readonly Stack<StrokeCollection> _redoStack = new();
    private bool _isApplyingHistory = false;

    // Active drawing settings
    private Color _currentPenColor = Color.FromRgb(15, 23, 42); // Black
    private double _currentStrokeThickness = 2.0;
    private bool _isSidebarVisible = true;

    private enum ModalMode { NewNotebook, NewSection }
    private ModalMode _activeModalMode = ModalMode.NewNotebook;

    public MainWindow()
    {
        InitializeComponent();
        _storageService = new NoteStorageService();

        ConfigureInkingDefaults();
        LoadInitialNotebooks();
    }

    private void ConfigureInkingDefaults()
    {
        // Enable hardware stylus pressure sensitivity & curve smoothing for Lenovo Yoga Pen
        MainInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _currentPenColor,
            Width = _currentStrokeThickness,
            Height = _currentStrokeThickness,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse
        };

        // Listen for Lenovo Stylus inverted eraser tip
        MainInkCanvas.StylusDown += MainInkCanvas_StylusDown;
        MainInkCanvas.StylusButtonDown += MainInkCanvas_StylusButtonDown;
        MainInkCanvas.StylusButtonUp += MainInkCanvas_StylusButtonUp;

        // Default tool is Type mode (OneNote behavior)
        MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
    }

    #region Stylus Hardware Handling (Lenovo Yoga Active Pen)

    private void MainInkCanvas_StylusDown(object sender, StylusDownEventArgs e)
    {
        // When stylus is inverted (eraser tip), switch to erase mode
        if (e.StylusDevice != null && e.StylusDevice.Inverted)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

    private void MainInkCanvas_StylusButtonDown(object sender, StylusButtonEventArgs e)
    {
        // Stylus barrel button (often used as eraser or lasso select in OneNote)
        if (e.StylusButton.Guid == StylusPointProperties.NormalPressure.Id || e.StylusButton.Name.Contains("Barrel", StringComparison.OrdinalIgnoreCase))
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

    private void MainInkCanvas_StylusButtonUp(object sender, StylusButtonEventArgs e)
    {
        RestoreCurrentToolMode();
    }

    #endregion

    #region Notebook / Section / Page Loading

    private void LoadInitialNotebooks()
    {
        _notebooks = _storageService.LoadNotebooks();

        ComboNotebooks.ItemsSource = _notebooks;
        if (_notebooks.Count > 0)
        {
            ComboNotebooks.SelectedIndex = 0;
        }
    }

    private void ComboNotebooks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrentPage();

        _currentNotebook = ComboNotebooks.SelectedItem as Notebook;
        if (_currentNotebook != null)
        {
            ListSections.ItemsSource = _currentNotebook.Sections;
            if (_currentNotebook.Sections.Count > 0)
            {
                ListSections.SelectedIndex = 0;
            }
            else
            {
                ListPages.ItemsSource = null;
                ClearCanvasView();
            }
        }
    }

    private void ListSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrentPage();

        _currentSection = ListSections.SelectedItem as Section;
        if (_currentSection != null)
        {
            ListPages.ItemsSource = _currentSection.Pages;
            if (_currentSection.Pages.Count > 0)
            {
                ListPages.SelectedIndex = 0;
            }
            else
            {
                ClearCanvasView();
            }
        }
    }

    private void ListPages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListPages.SelectedItem is PageItem pageItem)
        {
            if (_currentPageItem?.Id == pageItem.Id) return;

            SaveCurrentPage();
            _currentPageItem = pageItem;
            LoadPageContent(pageItem.Id, pageItem.Title);
        }
    }

    private void LoadPageContent(string pageId, string fallbackTitle)
    {
        _currentPageData = _storageService.LoadPage(pageId, fallbackTitle);

        TxtPageTitle.Text = _currentPageData.Title;
        TxtPageTimestamp.Text = _currentPageData.CreatedAt.ToString("dddd, MMMM dd, yyyy  h:mm tt");

        // Set paper background
        SetPaperStyle(_currentPageData.PaperStyle, updateDropdown: true);

        // Load ink strokes
        _isApplyingHistory = true;
        MainInkCanvas.Strokes.Clear();
        var strokes = NoteStorageService.DeserializeStrokes(_currentPageData.StrokeData);
        if (strokes != null && strokes.Count > 0)
        {
            MainInkCanvas.Strokes.Add(strokes);
        }
        _undoStack.Clear();
        _redoStack.Clear();
        RecordInkState();
        _isApplyingHistory = false;

        // Load floating text boxes
        MainInkCanvas.Children.Clear();
        if (_currentPageData.TextBoxes != null)
        {
            foreach (var noteBox in _currentPageData.TextBoxes)
            {
                AddFloatingNoteToCanvas(noteBox);
            }
        }
    }

    private void SaveCurrentPage()
    {
        if (_currentPageData == null || _currentPageItem == null) return;

        // Sync title
        _currentPageData.Title = TxtPageTitle.Text;
        _currentPageItem.Title = TxtPageTitle.Text;

        // Sync strokes
        _currentPageData.StrokeData = NoteStorageService.SerializeStrokes(MainInkCanvas.Strokes);

        // Sync text boxes
        var boxList = new List<NoteTextBox>();
        foreach (UIElement child in MainInkCanvas.Children)
        {
            if (child is FloatingNoteControl noteCtrl)
            {
                boxList.Add(noteCtrl.Model);
            }
        }
        _currentPageData.TextBoxes = boxList;

        _storageService.SavePage(_currentPageData);
        _storageService.SaveNotebooks(_notebooks);
    }

    private void ClearCanvasView()
    {
        TxtPageTitle.Text = string.Empty;
        TxtPageTimestamp.Text = string.Empty;
        MainInkCanvas.Strokes.Clear();
        MainInkCanvas.Children.Clear();
        _currentPageItem = null;
        _currentPageData = null;
    }

    #endregion

    #region Freeform Canvas & Text Anywhere

    private void MainInkCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Check if user clicked on an existing floating note
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit != MainInkCanvas)
        {
            if (hit is FloatingNoteControl) return; // Clicking an existing note
            hit = VisualTreeHelper.GetParent(hit);
        }

        // Double click anywhere OR single click in Type mode creates a text box
        if (e.ClickCount >= 2 || ToolText.IsChecked == true)
        {
            var pos = e.GetPosition(MainInkCanvas);
            CreateAndAddNoteAt(pos.X, pos.Y);
            e.Handled = true;
        }
    }

    private void CreateAndAddNoteAt(double x, double y)
    {
        var model = new NoteTextBox
        {
            X = Math.Max(10, x),
            Y = Math.Max(10, y),
            Width = 260,
            Height = 110,
            Text = string.Empty
        };

        var noteCtrl = AddFloatingNoteToCanvas(model);
        noteCtrl.FocusEditor();
    }

    private FloatingNoteControl AddFloatingNoteToCanvas(NoteTextBox model)
    {
        var control = new FloatingNoteControl(model);
        control.DeleteRequested += (s, ctrl) =>
        {
            MainInkCanvas.Children.Remove(ctrl);
            SaveCurrentPage();
        };
        control.ContentChanged += (s, e) => SaveCurrentPage();
        control.PositionChanged += (s, e) => SaveCurrentPage();

        MainInkCanvas.Children.Add(control);
        return control;
    }

    private void TxtPageTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentPageItem != null && _currentPageData != null)
        {
            _currentPageItem.Title = TxtPageTitle.Text;
            _currentPageData.Title = TxtPageTitle.Text;

            // Trigger list refresh
            ListPages.Items.Refresh();
        }
    }

    #endregion

    #region Inking Tools & Modes

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreCurrentToolMode();
    }

    private void RestoreCurrentToolMode()
    {
        if (MainInkCanvas == null || ToolText == null || ToolPen == null || ToolHighlighter == null || ToolEraser == null || ToolSelect == null)
            return;

        if (ToolText.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
            MainInkCanvas.Cursor = Cursors.IBeam;
        }
        else if (ToolPen.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.Height = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
        }
        else if (ToolHighlighter.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = true;
            // Semi-transparent color for highlighter
            var hlColor = Color.FromArgb(120, _currentPenColor.R, _currentPenColor.G, _currentPenColor.B);
            if (hlColor.R == 15 && hlColor.G == 23 && hlColor.B == 42) // If black, default to yellow
            {
                hlColor = Color.FromArgb(120, 250, 204, 21);
            }
            MainInkCanvas.DefaultDrawingAttributes.Color = hlColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _currentStrokeThickness * 3;
            MainInkCanvas.DefaultDrawingAttributes.Height = _currentStrokeThickness * 6;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Rectangle;
        }
        else if (ToolEraser.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            MainInkCanvas.Cursor = Cursors.Cross;
        }
        else if (ToolSelect.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Select;
            MainInkCanvas.Cursor = Cursors.Arrow;
        }
    }

    private void ColorChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Background is SolidColorBrush brush)
        {
            _currentPenColor = brush.Color;

            // If user clicked a color, automatically switch to Pen or Highlighter
            if (ToolText.IsChecked == true || ToolEraser.IsChecked == true || ToolSelect.IsChecked == true)
            {
                ToolPen.IsChecked = true;
            }

            RestoreCurrentToolMode();
        }
    }

    private void ComboStrokeThickness_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboStrokeThickness.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (double.TryParse(item.Tag.ToString(), out var thickness))
            {
                _currentStrokeThickness = thickness;
                RestoreCurrentToolMode();
            }
        }
    }

    private void ComboPaperStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboPaperStyle.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (Enum.TryParse<PaperStyle>(item.Tag.ToString(), out var style))
            {
                SetPaperStyle(style, updateDropdown: false);
            }
        }
    }

    private void SetPaperStyle(PaperStyle style, bool updateDropdown)
    {
        if (PaperBackgroundBorder == null) return;

        if (_currentPageData != null)
        {
            _currentPageData.PaperStyle = style;
        }

        PaperBackgroundBorder.Background = PaperBackgroundHelper.GetPaperBrush(style);

        if (updateDropdown)
        {
            foreach (ComboBoxItem item in ComboPaperStyle.Items)
            {
                if (item.Tag?.ToString() == style.ToString())
                {
                    ComboPaperStyle.SelectedItem = item;
                    break;
                }
            }
        }
    }

    #endregion

    #region Ink Undo / Redo & Editing History

    private void MainInkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (!_isApplyingHistory)
        {
            RecordInkState();
            _redoStack.Clear();
        }
    }

    private void MainInkCanvas_StrokeErased(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingHistory)
        {
            RecordInkState();
            _redoStack.Clear();
        }
    }

    private void RecordInkState()
    {
        var copy = new StrokeCollection();
        foreach (var stroke in MainInkCanvas.Strokes)
        {
            copy.Add(stroke.Clone());
        }
        _undoStack.Push(copy);
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count > 1)
        {
            _isApplyingHistory = true;
            var current = _undoStack.Pop();
            _redoStack.Push(current);

            var previous = _undoStack.Peek();
            MainInkCanvas.Strokes.Clear();
            foreach (var s in previous)
            {
                MainInkCanvas.Strokes.Add(s.Clone());
            }
            _isApplyingHistory = false;
        }
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_redoStack.Count > 0)
        {
            _isApplyingHistory = true;
            var next = _redoStack.Pop();
            _undoStack.Push(next);

            MainInkCanvas.Strokes.Clear();
            foreach (var s in next)
            {
                MainInkCanvas.Strokes.Add(s.Clone());
            }
            _isApplyingHistory = false;
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "Are you sure you want to clear all ink on this page?", "Clear Canvas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            MainInkCanvas.Strokes.Clear();
            RecordInkState();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var saveDlg = new SaveFileDialog
        {
            Title = "Export Note as Image",
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"{_currentPageItem?.Title ?? "YogaNote"}.png"
        };

        if (saveDlg.ShowDialog(this) == true)
        {
            try
            {
                NoteStorageService.ExportToPng(CanvasContainer, saveDlg.FileName);
                MessageBox.Show(this, $"Note exported successfully to:\n{saveDlg.FileName}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export image: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region New Notebook / Section / Page Actions

    private void BtnNewPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection == null) return;

        var newPage = new PageItem
        {
            Title = $"Page {_currentSection.Pages.Count + 1}",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        _currentSection.Pages.Add(newPage);
        ListPages.Items.Refresh();
        ListPages.SelectedItem = newPage;
    }

    private void BtnDeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pageId && _currentSection != null)
        {
            var pageToDelete = _currentSection.Pages.FirstOrDefault(p => p.Id == pageId);
            if (pageToDelete != null)
            {
                var result = MessageBox.Show(this, $"Delete page \"{pageToDelete.Title}\"?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _currentSection.Pages.Remove(pageToDelete);
                    _storageService.DeletePageFile(pageId);
                    ListPages.Items.Refresh();

                    if (_currentSection.Pages.Count > 0)
                    {
                        ListPages.SelectedIndex = 0;
                    }
                    else
                    {
                        ClearCanvasView();
                    }
                    SaveCurrentPage();
                }
            }
        }
    }

    private void BtnNewNotebook_Click(object sender, RoutedEventArgs e)
    {
        _activeModalMode = ModalMode.NewNotebook;
        ModalTitle.Text = "Create New Notebook";
        ModalPrompt.Text = "Enter notebook name:";
        ModalInput.Text = "My Notes";
        ModalOverlay.Visibility = Visibility.Visible;
        ModalInput.Focus();
        ModalInput.SelectAll();
    }

    private void BtnNewSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNotebook == null) return;
        _activeModalMode = ModalMode.NewSection;
        ModalTitle.Text = "Create New Section";
        ModalPrompt.Text = "Enter section name:";
        ModalInput.Text = "New Section";
        ModalOverlay.Visibility = Visibility.Visible;
        ModalInput.Focus();
        ModalInput.SelectAll();
    }

    private void ModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        var input = ModalInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        ModalOverlay.Visibility = Visibility.Collapsed;

        if (_activeModalMode == ModalMode.NewNotebook)
        {
            var initialPage = new PageItem { Title = "Untitled Page", CreatedAt = DateTime.Now };
            var initialSection = new Section
            {
                Name = "General",
                ColorHex = "#7719AA",
                Pages = new List<PageItem> { initialPage }
            };
            var newNotebook = new Notebook
            {
                Name = input,
                Sections = new List<Section> { initialSection }
            };

            _notebooks.Add(newNotebook);
            ComboNotebooks.Items.Refresh();
            ComboNotebooks.SelectedItem = newNotebook;
            _storageService.SaveNotebooks(_notebooks);
        }
        else if (_activeModalMode == ModalMode.NewSection && _currentNotebook != null)
        {
            var initialPage = new PageItem { Title = "Untitled Page", CreatedAt = DateTime.Now };
            var sectionColors = new[] { "#7719AA", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#EC4899" };
            var color = sectionColors[_currentNotebook.Sections.Count % sectionColors.Length];

            var newSection = new Section
            {
                Name = input,
                ColorHex = color,
                Pages = new List<PageItem> { initialPage }
            };

            _currentNotebook.Sections.Add(newSection);
            ListSections.Items.Refresh();
            ListSections.SelectedItem = newSection;
            _storageService.SaveNotebooks(_notebooks);
        }
    }

    private void ModalCancel_Click(object sender, RoutedEventArgs e)
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarVisible = !_isSidebarVisible;
        SidebarColDef.Width = _isSidebarVisible ? new GridLength(310) : new GridLength(0);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentPage();
        base.OnClosing(e);
    }

    #endregion
}
