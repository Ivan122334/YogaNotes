using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YogaNotes.Controls;
using YogaNotes.Models;
using YogaNotes.Services;

namespace YogaNotes;

public partial class MainWindow : Window
{
    private readonly NoteStorageService _storageService;
    private readonly HandwritingRecognitionService _handwritingService;
    private readonly DispatcherTimer _handwritingTimer;
    private readonly List<Stroke> _pendingHandwritingStrokes = new();

    private List<Notebook> _notebooks = new();
    private Notebook? _currentNotebook;
    private Section? _currentSection;
    private PageItem? _currentPageItem;
    private PageData? _currentPageData;

    // Ink undo / redo history
    private readonly Stack<StrokeCollection> _undoStack = new();
    private readonly Stack<StrokeCollection> _redoStack = new();
    private bool _isApplyingHistory = false;

    // Active drawing settings (separate colors for Pen vs Highlighter)
    private Color _currentPenColor = Color.FromRgb(15, 23, 42);        // Black
    private Color _currentHighlighterColor = Color.FromRgb(250, 204, 21); // Bright Yellow
    private double _currentStrokeThickness = 2.0;
    private HandwritingLanguage _handwritingLanguage = HandwritingLanguage.English;

    // Responsive sidebar state
    private bool _isSidebarVisible = true;
    private bool _userManuallyToggledSidebar = false;

    private enum ModalMode { NewNotebook, NewSection }
    private ModalMode _activeModalMode = ModalMode.NewNotebook;

    public MainWindow()
    {
        InitializeComponent();
        _storageService = new NoteStorageService();
        _handwritingService = new HandwritingRecognitionService();

        _handwritingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1300)
        };
        _handwritingTimer.Tick += async (_, _) =>
        {
            _handwritingTimer.Stop();
            await ConvertPendingHandwritingAsync();
        };

        ColorWheelControl.ColorSelected += ColorWheelControl_ColorSelected;

        ConfigureInkingDefaults();
        LoadInitialNotebooks();
        RestoreCurrentToolMode();
    }

    private void ConfigureInkingDefaults()
    {
        MainInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _currentPenColor,
            Width = _currentStrokeThickness,
            Height = _currentStrokeThickness,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse
        };

        // Listen for Lenovo Stylus inverted eraser tip and barrel button
        MainInkCanvas.StylusDown += MainInkCanvas_StylusDown;
        MainInkCanvas.StylusButtonDown += MainInkCanvas_StylusButtonDown;
        MainInkCanvas.StylusButtonUp += MainInkCanvas_StylusButtonUp;

        MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
    }

    #region Responsive Layout Handling

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColDef == null || PageHeaderPanel == null) return;

        // Auto-collapse sidebar on narrow/portrait Lenovo Yoga screens unless user explicitly toggled it
        if (!_userManuallyToggledSidebar)
        {
            bool shouldShow = ActualWidth >= 860;
            if (shouldShow != _isSidebarVisible)
            {
                _isSidebarVisible = shouldShow;
                ApplySidebarVisibility();
            }
        }

        // Keep page title header cleanly sized to the available canvas viewport
        double sidebarWidth = _isSidebarVisible ? SidebarColDef.Width.Value : 0;
        double availableWidth = Math.Max(320, ActualWidth - sidebarWidth - 110);
        PageHeaderPanel.Width = Math.Min(900, availableWidth);
    }

    private void ApplySidebarVisibility()
    {
        if (_isSidebarVisible)
        {
            SidebarColDef.Width = new GridLength(290);
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColDef.Width = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _userManuallyToggledSidebar = true;
        _isSidebarVisible = !_isSidebarVisible;
        ApplySidebarVisibility();
    }

    #endregion

    #region Stylus Hardware Handling (Lenovo Yoga Active Pen)

    private void MainInkCanvas_StylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice != null && e.StylusDevice.Inverted)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

    private void MainInkCanvas_StylusButtonDown(object sender, StylusButtonEventArgs e)
    {
        if (e.StylusButton.Guid == StylusPointProperties.NormalPressure.Id ||
            e.StylusButton.Name.Contains("Barrel", StringComparison.OrdinalIgnoreCase))
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
        _handwritingTimer.Stop();
        _pendingHandwritingStrokes.Clear();

        _currentPageData = _storageService.LoadPage(pageId, fallbackTitle);

        TxtPageTitle.Text = _currentPageData.Title;
        TxtPageTimestamp.Text = _currentPageData.CreatedAt.ToString("dddd, MMMM dd, yyyy  h:mm tt");

        SetPaperStyle(_currentPageData.PaperStyle, updateDropdown: true);

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

        _currentPageData.Title = TxtPageTitle.Text;
        _currentPageItem.Title = TxtPageTitle.Text;
        _currentPageData.StrokeData = NoteStorageService.SerializeStrokes(MainInkCanvas.Strokes);

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
        // ONLY create floating note bubbles when explicitly in Type mode!
        // Never create note bubbles while using Pen, Highlighter, Shape, Write->Text, Eraser, or Lasso.
        if (ToolText.IsChecked != true) return;

        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit != MainInkCanvas)
        {
            if (hit is FloatingNoteControl) return; // Interacting with an existing note
            hit = VisualTreeHelper.GetParent(hit);
        }

        var pos = e.GetPosition(MainInkCanvas);
        CreateAndAddNoteAt(pos.X, pos.Y);
        e.Handled = true;
    }

    private FloatingNoteControl CreateAndAddNoteAt(double x, double y, string initialText = "", bool focus = true)
    {
        var model = new NoteTextBox
        {
            X = Math.Max(10, x),
            Y = Math.Max(10, y),
            Width = 280,
            Height = 110,
            Text = initialText
        };

        var noteCtrl = AddFloatingNoteToCanvas(model);
        if (focus) noteCtrl.FocusEditor();
        SaveCurrentPage();
        return noteCtrl;
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
            ListPages.Items.Refresh();
        }
    }

    #endregion

    #region Inking Tools, Colors, Wheel & Modes

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        // If switching away from Write->Text while strokes are pending, convert them first
        if (ToolWriteText.IsChecked != true && _pendingHandwritingStrokes.Count > 0)
        {
            _handwritingTimer.Stop();
            _ = ConvertPendingHandwritingAsync();
        }

        RestoreCurrentToolMode();
    }

    private void RestoreCurrentToolMode()
    {
        if (MainInkCanvas == null || ToolText == null || ToolPen == null ||
            ToolHighlighter == null || ToolEraser == null || ToolSelect == null ||
            ToolWriteText == null || ToolShape == null)
            return;

        bool isHighlighter = ToolHighlighter.IsChecked == true;
        bool isWriteMode = ToolWriteText.IsChecked == true;

        // Show Write->Text language controls only in Writing mode
        if (WriteModeControlsPanel != null)
            WriteModeControlsPanel.Visibility = isWriteMode ? Visibility.Visible : Visibility.Collapsed;

        // When Highlighter is chosen, remove dark/pen colors and show only bright highlighter colors
        if (PenColorsPanel != null && HighlighterColorsPanel != null)
        {
            PenColorsPanel.Visibility = isHighlighter ? Visibility.Collapsed : Visibility.Visible;
            HighlighterColorsPanel.Visibility = isHighlighter ? Visibility.Visible : Visibility.Collapsed;
        }

        var activeColor = isHighlighter ? _currentHighlighterColor : _currentPenColor;
        UpdateActiveColorIndicators(activeColor);

        if (ToolText.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
            MainInkCanvas.Cursor = Cursors.IBeam;
            UpdateStatus("Type Mode — Click anywhere on the canvas to create a floating note");
        }
        else if (ToolPen.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.Height = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
            UpdateStatus("Pen Mode — Smooth pressure-sensitive inking (note bubbles disabled)");
        }
        else if (ToolWriteText.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(2.5, _currentStrokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(2.5, _currentStrokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
            var langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";
            UpdateStatus($"Writing Mode ({langName}) — Write cursive with your pen; pauses automatically convert to text");
        }
        else if (ToolShape.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Cross;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = false;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.Height = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
            UpdateStatus("Shape Mode — Draw a circle, oval, square, rectangle, triangle, or straight line to snap cleanly");
        }
        else if (ToolHighlighter.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = true;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = Color.FromArgb(120, _currentHighlighterColor.R, _currentHighlighterColor.G, _currentHighlighterColor.B);
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(8, _currentStrokeThickness * 3);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(18, _currentStrokeThickness * 6);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Rectangle;
            UpdateStatus("Highlighter Mode — Translucent bright highlight colors");
        }
        else if (ToolEraser.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            MainInkCanvas.Cursor = Cursors.Cross;
            UpdateStatus("Eraser Mode — Tap or drag across ink strokes to erase");
        }
        else if (ToolSelect.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Select;
            MainInkCanvas.Cursor = Cursors.Arrow;
            UpdateStatus("Lasso Mode — Circle ink strokes to select, move, or resize");
        }
    }

    private void UpdateActiveColorIndicators(Color color)
    {
        if (ActiveColorDot != null)
            ActiveColorDot.Fill = new SolidColorBrush(color);

        if (StatusColorText != null)
            StatusColorText.Text = $"Color: #{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void UpdateStatus(string text)
    {
        if (StatusModeText != null)
            StatusModeText.Text = text;
    }

    private void ColorChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Background is SolidColorBrush brush)
        {
            ApplyChosenColor(brush.Color);
        }
    }

    private void BtnColorWheel_Click(object sender, RoutedEventArgs e)
    {
        var current = ToolHighlighter.IsChecked == true ? _currentHighlighterColor : _currentPenColor;
        ColorWheelControl.SetCurrentColor(current);
        ColorWheelPopup.IsOpen = !ColorWheelPopup.IsOpen;
    }

    private void ColorWheelControl_ColorSelected(object? sender, Color color)
    {
        ApplyChosenColor(color);
    }

    private void ApplyChosenColor(Color color)
    {
        if (ToolHighlighter.IsChecked == true)
        {
            _currentHighlighterColor = color;
        }
        else
        {
            _currentPenColor = color;
            // If user picked a color while in Type/Eraser/Lasso, switch to Pen automatically
            if (ToolText.IsChecked == true || ToolEraser.IsChecked == true || ToolSelect.IsChecked == true)
            {
                ToolPen.IsChecked = true;
            }
        }

        RestoreCurrentToolMode();
    }

    private void ComboWriteLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboWriteLanguage?.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (Enum.TryParse<HandwritingLanguage>(item.Tag.ToString(), out var lang))
            {
                _handwritingLanguage = lang;
                RestoreCurrentToolMode();
            }
        }
    }

    private async void BtnConvertNow_Click(object sender, RoutedEventArgs e)
    {
        _handwritingTimer.Stop();
        await ConvertPendingHandwritingAsync();
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

    #region Ink Events, Shape Recognition & Handwriting-to-Text

    private void MainInkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_isApplyingHistory) return;

        // 1. Writing Mode (Handwriting -> Text in English or Bulgarian)
        if (ToolWriteText.IsChecked == true)
        {
            _pendingHandwritingStrokes.Add(e.Stroke);
            _handwritingTimer.Stop();
            _handwritingTimer.Start();
            var langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";
            UpdateStatus($"Writing Mode ({langName}) — Recognizing handwriting...");
            return;
        }

        // 2. Shape Mode (Paint-style crisp shape snapping)
        if (ToolShape.IsChecked == true)
        {
            var cleanedShape = ShapeRecognizer.TryRecognizeAndCleanShape(e.Stroke);
            if (cleanedShape != null)
            {
                _isApplyingHistory = true;
                MainInkCanvas.Strokes.Remove(e.Stroke);
                MainInkCanvas.Strokes.Add(cleanedShape);
                _isApplyingHistory = false;
                UpdateStatus("Shape Mode — Snapped to clean geometric shape!");
            }
        }

        RecordInkState();
        _redoStack.Clear();
    }

    private async Task ConvertPendingHandwritingAsync()
    {
        if (_pendingHandwritingStrokes.Count == 0) return;

        var strokesToProcess = _pendingHandwritingStrokes.ToList();
        _pendingHandwritingStrokes.Clear();

        // Compute bounding box of the handwritten word/phrase so text appears right where user wrote
        Rect combinedBounds = Rect.Empty;
        foreach (var s in strokesToProcess)
        {
            combinedBounds.Union(s.GetBounds());
        }

        string recognizedText = string.Empty;
        try
        {
            recognizedText = await _handwritingService.RecognizeStrokesAsync(strokesToProcess, _handwritingLanguage);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Handwriting recognition error: {ex.Message}");
            return;
        }

        // Remove the raw temporary handwriting strokes from canvas
        _isApplyingHistory = true;
        foreach (var s in strokesToProcess)
        {
            MainInkCanvas.Strokes.Remove(s);
        }
        _isApplyingHistory = false;

        var langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";

        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            UpdateStatus($"Writing Mode ({langName}) — Could not recognize text, try writing slightly larger");
            return;
        }

        // Check if user is writing right next to or inside an existing floating note
        FloatingNoteControl? targetNote = FindNearbyNote(combinedBounds);
        if (targetNote != null)
        {
            var existing = targetNote.NoteTextBoxControl.Text;
            var separator = string.IsNullOrWhiteSpace(existing) || existing.EndsWith(' ') || existing.EndsWith('\n')
                ? ""
                : " ";
            targetNote.NoteTextBoxControl.Text = existing + separator + recognizedText;
        }
        else
        {
            double x = combinedBounds.IsEmpty ? 60 : Math.Max(10, combinedBounds.X);
            double y = combinedBounds.IsEmpty ? 60 : Math.Max(10, combinedBounds.Y);
            CreateAndAddNoteAt(x, y, recognizedText, focus: false);
        }

        SaveCurrentPage();
        UpdateStatus($"Recognized ({langName}): \"{recognizedText}\"");
    }

    private FloatingNoteControl? FindNearbyNote(Rect strokeBounds)
    {
        if (strokeBounds.IsEmpty) return null;

        // Expand search zone around existing note boxes so writing on the same line appends cleanly
        foreach (UIElement child in MainInkCanvas.Children)
        {
            if (child is FloatingNoteControl note)
            {
                double nx = InkCanvas.GetLeft(note);
                double ny = InkCanvas.GetTop(note);
                if (double.IsNaN(nx)) nx = 0;
                if (double.IsNaN(ny)) ny = 0;

                var expandedRect = new Rect(nx - 30, ny - 25, note.Width + 140, note.Height + 60);
                if (expandedRect.IntersectsWith(strokeBounds))
                {
                    return note;
                }
            }
        }
        return null;
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
            _handwritingTimer.Stop();
            _pendingHandwritingStrokes.Clear();
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentPage();
        base.OnClosing(e);
    }

    #endregion
}
