using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YogaNotes.Models;
using YogaNotes.Services;
using Section = YogaNotes.Models.Section;

namespace YogaNotes;

public partial class MainWindow : Window
{
    private readonly NoteStorageService _storageService;
    private readonly HandwritingRecognitionService _handwritingService;
    private readonly DispatcherTimer _handwritingTimer;
    private readonly DispatcherTimer _autoSaveTimer;
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
    private bool _isLoadingPage = false;
    private bool _isSyncingFormattingUI = false;

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

        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SaveCurrentPage();
        };

        ColorWheelControl.ColorSelected += ColorWheelControl_ColorSelected;

        ConfigureInkingDefaults();
        LoadInitialNotebooks();
        ApplyActiveMode();
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

        // Lenovo Active Pen hardware eraser tip & barrel button
        MainInkCanvas.StylusDown += MainInkCanvas_StylusDown;
        MainInkCanvas.StylusButtonDown += MainInkCanvas_StylusButtonDown;
        MainInkCanvas.StylusButtonUp += MainInkCanvas_StylusButtonUp;
    }

    #region Responsive Layout & Keyboard Shortcuts

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColDef == null || PageSheetContainer == null) return;

        // Auto-collapse sidebar on narrow/portrait Lenovo Yoga screens unless user toggled manually
        if (!_userManuallyToggledSidebar)
        {
            bool shouldShow = ActualWidth >= 880;
            if (shouldShow != _isSidebarVisible)
            {
                _isSidebarVisible = shouldShow;
                ApplySidebarVisibility();
            }
        }

        // Keep Word-like page sheet centered at 820px on wide screens, scaling down smoothly on narrow screens
        double sidebarWidth = _isSidebarVisible ? SidebarColDef.Width.Value : 0;
        double available = Math.Max(360, ActualWidth - sidebarWidth - 76);
        PageSheetContainer.Width = Math.Min(820, available);
    }

    private void ApplySidebarVisibility()
    {
        if (_isSidebarVisible)
        {
            SidebarColDef.Width = new GridLength(280);
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Route Ctrl+Z / Ctrl+Y to Ink Undo/Redo when in Draw mode (Type mode uses RichTextBox built-in Undo/Redo)
        if (ModeDraw.IsChecked == true && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
            {
                UndoInk();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                RedoInk();
                e.Handled = true;
            }
        }
    }

    #endregion

    #region Stylus Hardware Handling (Lenovo Yoga Active Pen)

    private void MainInkCanvas_StylusDown(object sender, StylusDownEventArgs e)
    {
        if (ModeDraw.IsChecked == true && e.StylusDevice != null && e.StylusDevice.Inverted)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

    private void MainInkCanvas_StylusButtonDown(object sender, StylusButtonEventArgs e)
    {
        if (ModeDraw.IsChecked == true &&
            (e.StylusButton.Guid == StylusPointProperties.NormalPressure.Id ||
             e.StylusButton.Name.Contains("Barrel", StringComparison.OrdinalIgnoreCase)))
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

    private void MainInkCanvas_StylusButtonUp(object sender, StylusButtonEventArgs e)
    {
        if (ModeDraw.IsChecked == true)
        {
            ApplyDrawSubTool();
        }
    }

    #endregion

    #region Notebook / Section / Page Loading & Persistence

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
        _isLoadingPage = true;
        _autoSaveTimer.Stop();
        _handwritingTimer.Stop();
        _pendingHandwritingStrokes.Clear();

        _currentPageData = _storageService.LoadPage(pageId, fallbackTitle);

        TxtPageTitle.Text = _currentPageData.Title;
        TxtPageTimestamp.Text = _currentPageData.CreatedAt.ToString("dddd, MMMM dd, yyyy  h:mm tt");

        SetPaperStyle(_currentPageData.PaperStyle, updateDropdown: true);

        // Load unified Word-style FlowDocument (Times New Roman 12pt default)
        DocumentEditor.Document = NoteStorageService.DeserializeFlowDocument(
            _currentPageData.DocumentXaml,
            _currentPageData.DocumentPlainText);

        // Load ink strokes layered on the same page
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

        _isLoadingPage = false;
    }

    private void SaveCurrentPage()
    {
        if (_isLoadingPage || _currentPageData == null || _currentPageItem == null) return;

        _currentPageData.Title = TxtPageTitle.Text;
        _currentPageItem.Title = TxtPageTitle.Text;

        var (xaml, plainText) = NoteStorageService.SerializeFlowDocument(DocumentEditor.Document);
        _currentPageData.DocumentXaml = xaml;
        _currentPageData.DocumentPlainText = plainText;

        _currentPageData.StrokeData = NoteStorageService.SerializeStrokes(MainInkCanvas.Strokes);

        _storageService.SavePage(_currentPageData);
        _storageService.SaveNotebooks(_notebooks);
    }

    private void ClearCanvasView()
    {
        _isLoadingPage = true;
        TxtPageTitle.Text = string.Empty;
        TxtPageTimestamp.Text = string.Empty;
        DocumentEditor.Document = NoteStorageService.CreateStyledDocument();
        MainInkCanvas.Strokes.Clear();
        _currentPageItem = null;
        _currentPageData = null;
        _isLoadingPage = false;
    }

    #endregion

    #region Word-Style Document Editing & Formatting

    private void DocumentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingPage) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void DocumentEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingPage || DocumentEditor == null) return;

        _isSyncingFormattingUI = true;
        try
        {
            var weight = DocumentEditor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
            BtnBold.IsChecked = weight != DependencyProperty.UnsetValue && weight.Equals(FontWeights.Bold);

            var style = DocumentEditor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
            BtnItalic.IsChecked = style != DependencyProperty.UnsetValue && style.Equals(FontStyles.Italic);

            var dec = DocumentEditor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            BtnUnderline.IsChecked = dec != DependencyProperty.UnsetValue && dec.Equals(TextDecorations.Underline);

            var sizeObj = DocumentEditor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
            if (sizeObj is double sizeDip)
            {
                int pt = (int)Math.Round(DocumentTypography.DipToPt(sizeDip));
                foreach (ComboBoxItem item in ComboFontSize.Items)
                {
                    if (item.Tag?.ToString() == pt.ToString())
                    {
                        ComboFontSize.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        finally
        {
            _isSyncingFormattingUI = false;
        }
    }

    private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFormattingUI || DocumentEditor == null) return;

        if (ComboFontSize.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out double pt))
        {
            double dip = DocumentTypography.PtToDip(pt);
            DocumentEditor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, dip);
            DocumentEditor.Focus();
            UpdateRightStatus($"Times New Roman {pt:0} pt");
        }
    }

    private void TxtPageTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingPage || _currentPageItem == null || _currentPageData == null) return;

        _currentPageItem.Title = TxtPageTitle.Text;
        _currentPageData.Title = TxtPageTitle.Text;
        ListPages.Items.Refresh();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    #endregion

    #region Mode Switching (Type vs Draw) & Drawing Tools

    private void PrimaryMode_Click(object sender, RoutedEventArgs e)
    {
        // Flush any pending handwriting strokes if leaving Draw mode
        if (ModeType.IsChecked == true && _pendingHandwritingStrokes.Count > 0)
        {
            _handwritingTimer.Stop();
            _ = ConvertPendingHandwritingAsync();
        }

        ApplyActiveMode();
    }

    private void ApplyActiveMode()
    {
        if (ModeType == null || ModeDraw == null || MainInkCanvas == null || DocumentEditor == null)
            return;

        bool isTypeMode = ModeType.IsChecked == true;

        TypeToolbarPanel.Visibility = isTypeMode ? Visibility.Visible : Visibility.Collapsed;
        DrawToolbarPanel.Visibility = isTypeMode ? Visibility.Collapsed : Visibility.Visible;

        if (isTypeMode)
        {
            // Ink stays visible on the page, but pointer events go straight to the Word document
            MainInkCanvas.IsHitTestVisible = false;
            MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
            DocumentEditor.Focus();
            UpdateStatus("Type Mode — Continuous Word-style document (Times New Roman, 12 pt)");
            UpdateRightStatus("Times New Roman 12 pt");
        }
        else
        {
            // InkCanvas captures pen/mouse/touch directly over the document
            MainInkCanvas.IsHitTestVisible = true;
            ApplyDrawSubTool();
        }
    }

    private void DrawSubTool_Click(object sender, RoutedEventArgs e)
    {
        if (ToolWriteText.IsChecked != true && _pendingHandwritingStrokes.Count > 0)
        {
            _handwritingTimer.Stop();
            _ = ConvertPendingHandwritingAsync();
        }

        ApplyDrawSubTool();
    }

    private void ToggleShapeAssist_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ToggleShapeAssist.IsChecked == true;
        TxtShapeAssistLabel.Text = enabled ? "🔷 Shape Assist: ON" : "🔷 Shape Assist: OFF";
        ApplyDrawSubTool();
    }

    private void ApplyDrawSubTool()
    {
        if (MainInkCanvas == null || ToolPen == null || ToolWriteText == null ||
            ToolHighlighter == null || ToolEraser == null || ToolSelect == null)
            return;

        bool isHighlighter = ToolHighlighter.IsChecked == true;
        bool isWriteMode = ToolWriteText.IsChecked == true;
        bool isPenMode = ToolPen.IsChecked == true;

        WriteModeControlsPanel.Visibility = isWriteMode ? Visibility.Visible : Visibility.Collapsed;
        ToggleShapeAssist.Visibility = isPenMode ? Visibility.Visible : Visibility.Collapsed;

        // Highlighter hides dark pen colors and shows bright translucent highlighter chips
        PenColorsPanel.Visibility = isHighlighter ? Visibility.Collapsed : Visibility.Visible;
        HighlighterColorsPanel.Visibility = isHighlighter ? Visibility.Visible : Visibility.Collapsed;

        var activeColor = isHighlighter ? _currentHighlighterColor : _currentPenColor;
        UpdateActiveColorIndicators(activeColor);

        if (isPenMode)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.Height = _currentStrokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;

            string assistMsg = ToggleShapeAssist.IsChecked == true
                ? "Shape Assist ON (circles, lines, arrows, rectangles, triangles auto-enhance)"
                : "Freehand drawing (Shape Assist OFF)";
            UpdateStatus($"Draw Mode (Pen) — {assistMsg}");
        }
        else if (isWriteMode)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _currentPenColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(2.5, _currentStrokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(2.5, _currentStrokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;

            string langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";
            UpdateStatus($"Write→Text ({langName}) — Write cursive on the page; converts inline into Times New Roman document text");
        }
        else if (isHighlighter)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = true;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = Color.FromArgb(120, _currentHighlighterColor.R, _currentHighlighterColor.G, _currentHighlighterColor.B);
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(8, _currentStrokeThickness * 3);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(18, _currentStrokeThickness * 6);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Rectangle;
            UpdateStatus("Draw Mode (Highlighter) — Translucent bright highlight colors");
        }
        else if (ToolEraser.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            MainInkCanvas.Cursor = Cursors.Cross;
            UpdateStatus("Draw Mode (Eraser) — Tap or drag across ink strokes to erase");
        }
        else if (ToolSelect.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Select;
            MainInkCanvas.Cursor = Cursors.Arrow;
            UpdateStatus("Draw Mode (Lasso) — Circle ink strokes to select, move, or resize");
        }
    }

    private void UpdateActiveColorIndicators(Color color)
    {
        if (ActiveColorDot != null)
            ActiveColorDot.Fill = new SolidColorBrush(color);

        UpdateRightStatus($"Ink: #{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void UpdateStatus(string text)
    {
        if (StatusModeText != null)
            StatusModeText.Text = text;
    }

    private void UpdateRightStatus(string text)
    {
        if (StatusRightText != null)
            StatusRightText.Text = text;
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
            if (ToolEraser.IsChecked == true || ToolSelect.IsChecked == true)
            {
                ToolPen.IsChecked = true;
            }
        }

        ApplyDrawSubTool();
    }

    private void ComboWriteLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboWriteLanguage?.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (Enum.TryParse<HandwritingLanguage>(item.Tag.ToString(), out var lang))
            {
                _handwritingLanguage = lang;
                if (ModeDraw.IsChecked == true) ApplyDrawSubTool();
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
        if (ComboStrokeThickness.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out double thickness))
        {
            _currentStrokeThickness = thickness;
            if (ModeDraw?.IsChecked == true) ApplyDrawSubTool();
        }
    }

    private void ComboPaperStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboPaperStyle.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<PaperStyle>(item.Tag?.ToString(), out var style))
        {
            SetPaperStyle(style, updateDropdown: false);
            SaveCurrentPage();
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

    #region Ink Collection, Shape Auto-Enhancement & Inline Handwriting-to-Text

    private void MainInkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_isApplyingHistory) return;

        // 1. Write->Text Mode: collect strokes and schedule inline conversion into DocumentEditor
        if (ToolWriteText.IsChecked == true)
        {
            _pendingHandwritingStrokes.Add(e.Stroke);
            _handwritingTimer.Stop();
            _handwritingTimer.Start();
            string langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";
            UpdateStatus($"Write→Text ({langName}) — Recognizing handwriting...");
            return;
        }

        // 2. Pen Mode with Shape Assist ON: auto-enhance known shapes on pen-up
        if (ToolPen.IsChecked == true && ToggleShapeAssist.IsChecked == true)
        {
            var result = ShapeRecognizer.Analyze(e.Stroke);
            if (result.IsRecognized && result.CleanedStroke != null)
            {
                // Push raw stroke state FIRST so a single Ctrl+Z reverts the beautification back to raw ink!
                RecordInkState();

                _isApplyingHistory = true;
                MainInkCanvas.Strokes.Remove(e.Stroke);
                MainInkCanvas.Strokes.Add(result.CleanedStroke);
                _isApplyingHistory = false;

                RecordInkState();
                _redoStack.Clear();
                SaveCurrentPage();
                UpdateStatus($"Enhanced: {result.Kind} (Press Ctrl+Z once to revert to your original freehand stroke)");
                return;
            }
        }

        RecordInkState();
        _redoStack.Clear();
        SaveCurrentPage();
    }

    private async Task ConvertPendingHandwritingAsync()
    {
        if (_pendingHandwritingStrokes.Count == 0) return;

        var strokesToProcess = _pendingHandwritingStrokes.ToList();
        _pendingHandwritingStrokes.Clear();

        Rect combinedBounds = Rect.Empty;
        foreach (var s in strokesToProcess)
        {
            combinedBounds.Union(s.GetBounds());
        }

        string langName = _handwritingLanguage == HandwritingLanguage.Bulgarian ? "Български" : "English";
        List<string> recognizedLines;

        try
        {
            recognizedLines = await _handwritingService.RecognizeLinesAsync(strokesToProcess, _handwritingLanguage);
        }
        catch (Exception ex)
        {
            // Keep strokes untouched if recognition fails so user loses nothing
            UpdateStatus($"Handwriting recognition failed ({langName}): {ex.Message} — strokes kept on page");
            return;
        }

        if (recognizedLines.Count == 0)
        {
            // Keep strokes if nothing could be recognized
            UpdateStatus($"Write→Text ({langName}) — Could not recognize text; strokes kept on page. Try writing slightly larger.");
            return;
        }

        // Insert recognized lines directly into the unified Word-style DocumentEditor in Times New Roman
        InsertRecognizedLinesIntoDocument(recognizedLines, combinedBounds);

        // Remove the converted raw ink strokes so the text does not appear twice
        _isApplyingHistory = true;
        foreach (var s in strokesToProcess)
        {
            MainInkCanvas.Strokes.Remove(s);
        }
        _isApplyingHistory = false;

        RecordInkState();
        SaveCurrentPage();

        string preview = string.Join(" / ", recognizedLines);
        UpdateStatus($"Inserted into document ({langName}): \"{preview}\"");
    }

    /// <summary>
    /// Inserts recognized handwriting lines directly into DocumentEditor at the matching page location
    /// (or at the caret / end of document), formatted in Times New Roman 12pt and integrated with Undo/Redo.
    /// </summary>
    private void InsertRecognizedLinesIntoDocument(List<string> lines, Rect strokeBoundsOnInkCanvas)
    {
        TextPointer? insertPos = null;

        if (!strokeBoundsOnInkCanvas.IsEmpty)
        {
            var inkPoint = new Point(strokeBoundsOnInkCanvas.Left, strokeBoundsOnInkCanvas.Top + strokeBoundsOnInkCanvas.Height / 2.0);
            var editorPoint = MainInkCanvas.TranslatePoint(inkPoint, DocumentEditor);

            if (editorPoint.X >= 0 && editorPoint.Y >= 0 &&
                editorPoint.X <= DocumentEditor.ActualWidth &&
                editorPoint.Y <= DocumentEditor.ActualHeight)
            {
                insertPos = DocumentEditor.GetPositionFromPoint(editorPoint, snapToText: true);
            }
        }

        insertPos ??= DocumentEditor.CaretPosition ?? DocumentEditor.Document.ContentEnd;

        DocumentEditor.BeginChange();
        try
        {
            // If inserting into a non-empty paragraph and not at a space/start, add a space before the first line
            string firstLinePrefix = string.Empty;
            var prevChar = insertPos.GetTextInRun(LogicalDirection.Backward);
            if (!string.IsNullOrEmpty(prevChar) && !char.IsWhiteSpace(prevChar[^1]))
            {
                firstLinePrefix = " ";
            }

            TextPointer cursor = insertPos;
            for (int i = 0; i < lines.Count; i++)
            {
                string textToInsert = (i == 0 ? firstLinePrefix : string.Empty) + lines[i];

                if (i > 0)
                {
                    // Preserve handwriting line breaks as clean new paragraphs
                    cursor = cursor.InsertParagraphBreak();
                }

                cursor.InsertTextInRun(textToInsert);
                var insertedStart = cursor;
                var insertedEnd = cursor.GetPositionAtOffset(textToInsert.Length, LogicalDirection.Forward) ?? cursor;

                // Enforce central Times New Roman 12pt formatting on all inserted handwriting text
                var range = new TextRange(insertedStart, insertedEnd);
                range.ApplyPropertyValue(TextElement.FontFamilyProperty, DocumentTypography.DefaultFontFamily);
                range.ApplyPropertyValue(TextElement.FontSizeProperty, DocumentTypography.DefaultFontSizeDip);

                cursor = insertedEnd;
            }

            DocumentEditor.CaretPosition = cursor;
        }
        finally
        {
            DocumentEditor.EndChange();
        }
    }

    private void MainInkCanvas_StrokeErased(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingHistory)
        {
            RecordInkState();
            _redoStack.Clear();
            SaveCurrentPage();
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

    private void UndoInk()
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
            SaveCurrentPage();
        }
    }

    private void RedoInk()
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
            SaveCurrentPage();
        }
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (ModeType.IsChecked == true)
        {
            DocumentEditor.Undo();
            DocumentEditor.Focus();
        }
        else
        {
            UndoInk();
        }
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (ModeType.IsChecked == true)
        {
            DocumentEditor.Redo();
            DocumentEditor.Focus();
        }
        else
        {
            RedoInk();
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "Clear all ink drawings on this page? (Your typed text will remain untouched.)", "Clear Ink", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _handwritingTimer.Stop();
            _pendingHandwritingStrokes.Clear();
            MainInkCanvas.Strokes.Clear();
            RecordInkState();
            SaveCurrentPage();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var saveDlg = new SaveFileDialog
        {
            Title = "Export Page as Image",
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"{_currentPageItem?.Title ?? "YogaNote"}.png"
        };

        if (saveDlg.ShowDialog(this) == true)
        {
            try
            {
                NoteStorageService.ExportToPng(PageContentGrid, saveDlg.FileName);
                MessageBox.Show(this, $"Page exported successfully to:\n{saveDlg.FileName}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _autoSaveTimer.Stop();
        SaveCurrentPage();
        base.OnClosing(e);
    }

    #endregion
}
