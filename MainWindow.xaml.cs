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

    private readonly Stack<StrokeCollection> _undoStack = new();
    private readonly Stack<StrokeCollection> _redoStack = new();
    private bool _isApplyingHistory;
    private bool _isLoadingPage;
    private bool _isSyncingFormatting;

    private Color _penColor = Color.FromRgb(15, 23, 42);
    private Color _highlighterColor = Color.FromRgb(250, 204, 21);
    private double _strokeThickness = 2.0;
    private HandwritingLanguage _handwritingLanguage = HandwritingLanguage.English;

    private bool _isSidebarVisible = true;
    private bool _userToggledSidebar;

    private enum ModalMode { NewNotebook, NewSection }
    private ModalMode _modalMode = ModalMode.NewNotebook;

    public MainWindow()
    {
        InitializeComponent();
        _storageService = new NoteStorageService();
        _handwritingService = new HandwritingRecognitionService();

        RestoreWindowBounds();

        _handwritingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _handwritingTimer.Tick += async (_, _) =>
        {
            _handwritingTimer.Stop();
            await ConvertPendingHandwritingAsync();
        };

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SaveCurrentPage();
        };

        ColorWheelControl.ColorSelected += (_, color) => ApplyChosenColor(color);

        ConfigureInking();
        LoadNotebooks();
        ApplyActiveMode();
    }

    private void RestoreWindowBounds()
    {
        var saved = _storageService.LoadWindowSettings();
        if (saved.Width >= MinWidth && saved.Height >= MinHeight)
        {
            Width = saved.Width;
            Height = saved.Height;
        }

        if (!double.IsNaN(saved.Left) && !double.IsNaN(saved.Top))
        {
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100;
            double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
            if (saved.Left >= SystemParameters.VirtualScreenLeft - 50 && saved.Left <= maxLeft &&
                saved.Top >= SystemParameters.VirtualScreenTop - 50 && saved.Top <= maxTop)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left;
                Top = saved.Top;
            }
        }

        if (saved.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _storageService.SaveWindowSettings(new WindowSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized
        });
    }

    private void ConfigureInking()
    {
        MainInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor,
            Width = _strokeThickness,
            Height = _strokeThickness,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse
        };

        MainInkCanvas.StylusDown += MainInkCanvas_StylusDown;
        MainInkCanvas.StylusButtonDown += MainInkCanvas_StylusButtonDown;
        MainInkCanvas.StylusButtonUp += MainInkCanvas_StylusButtonUp;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColDef == null || PageSheetContainer == null) return;

        if (!_userToggledSidebar)
        {
            bool shouldShow = ActualWidth >= 880;
            if (shouldShow != _isSidebarVisible)
            {
                _isSidebarVisible = shouldShow;
                ApplySidebarVisibility();
            }
        }

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
        _userToggledSidebar = true;
        _isSidebarVisible = !_isSidebarVisible;
        ApplySidebarVisibility();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private void LoadNotebooks()
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
        if (_currentNotebook == null) return;

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

    private void ListSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrentPage();
        _currentSection = ListSections.SelectedItem as Section;
        if (_currentSection == null) return;

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

    private void ListPages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListPages.SelectedItem is not PageItem pageItem) return;
        if (_currentPageItem?.Id == pageItem.Id) return;

        SaveCurrentPage();
        _currentPageItem = pageItem;
        LoadPageContent(pageItem.Id, pageItem.Title);
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

        DocumentEditor.Document = NoteStorageService.DeserializeFlowDocument(
            _currentPageData.DocumentXaml,
            _currentPageData.DocumentPlainText);

        _isApplyingHistory = true;
        MainInkCanvas.Strokes.Clear();
        var strokes = NoteStorageService.DeserializeStrokes(_currentPageData.StrokeData);
        if (strokes.Count > 0)
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

    private void DocumentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingPage) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void DocumentEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingPage || DocumentEditor == null) return;

        _isSyncingFormatting = true;
        try
        {
            var weight = DocumentEditor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
            BtnBold.IsChecked = weight != DependencyProperty.UnsetValue && weight.Equals(FontWeights.Bold);

            var style = DocumentEditor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
            BtnItalic.IsChecked = style != DependencyProperty.UnsetValue && style.Equals(FontStyles.Italic);

            var dec = DocumentEditor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            BtnUnderline.IsChecked = dec != DependencyProperty.UnsetValue && dec.Equals(TextDecorations.Underline);

            if (DocumentEditor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double sizeDip)
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
            _isSyncingFormatting = false;
        }
    }

    private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFormatting || DocumentEditor == null) return;

        if (ComboFontSize.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out double pt))
        {
            DocumentEditor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, DocumentTypography.PtToDip(pt));
            DocumentEditor.Focus();
            StatusRightText.Text = $"Times New Roman {pt:0} pt";
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

    private void PrimaryMode_Click(object sender, RoutedEventArgs e)
    {
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
            // Keep drawings visible while letting clicks reach the document underneath.
            MainInkCanvas.IsHitTestVisible = false;
            MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
            DocumentEditor.Focus();
            StatusModeText.Text = "Type mode";
            StatusRightText.Text = "Times New Roman 12 pt";
        }
        else
        {
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
        TxtShapeAssistLabel.Text = ToggleShapeAssist.IsChecked == true ? "Shape assist: ON" : "Shape assist: OFF";
        ApplyDrawSubTool();
    }

    private void ApplyDrawSubTool()
    {
        if (MainInkCanvas == null || ToolPen == null) return;

        bool isHighlighter = ToolHighlighter.IsChecked == true;
        bool isWriteMode = ToolWriteText.IsChecked == true;
        bool isPenMode = ToolPen.IsChecked == true;

        WriteModeControlsPanel.Visibility = isWriteMode ? Visibility.Visible : Visibility.Collapsed;
        ToggleShapeAssist.Visibility = isPenMode ? Visibility.Visible : Visibility.Collapsed;
        PenColorsPanel.Visibility = isHighlighter ? Visibility.Collapsed : Visibility.Visible;
        HighlighterColorsPanel.Visibility = isHighlighter ? Visibility.Visible : Visibility.Collapsed;

        var activeColor = isHighlighter ? _highlighterColor : _penColor;
        ActiveColorDot.Fill = new SolidColorBrush(activeColor);
        StatusRightText.Text = $"#{activeColor.R:X2}{activeColor.G:X2}{activeColor.B:X2}";

        if (isPenMode)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _penColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = _strokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.Height = _strokeThickness;
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
            StatusModeText.Text = ToggleShapeAssist.IsChecked == true
                ? "Pen (shape assist on)"
                : "Pen (freehand)";
        }
        else if (isWriteMode)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = false;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = _penColor;
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(2.5, _strokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(2.5, _strokeThickness);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Ellipse;
            StatusModeText.Text = $"Write to text ({_handwritingLanguage})";
        }
        else if (isHighlighter)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MainInkCanvas.Cursor = Cursors.Pen;
            MainInkCanvas.DefaultDrawingAttributes.IsHighlighter = true;
            MainInkCanvas.DefaultDrawingAttributes.FitToCurve = true;
            MainInkCanvas.DefaultDrawingAttributes.Color = Color.FromArgb(120, _highlighterColor.R, _highlighterColor.G, _highlighterColor.B);
            MainInkCanvas.DefaultDrawingAttributes.Width = Math.Max(8, _strokeThickness * 3);
            MainInkCanvas.DefaultDrawingAttributes.Height = Math.Max(18, _strokeThickness * 6);
            MainInkCanvas.DefaultDrawingAttributes.StylusTip = StylusTip.Rectangle;
            StatusModeText.Text = "Highlighter";
        }
        else if (ToolEraser.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            MainInkCanvas.Cursor = Cursors.Cross;
            StatusModeText.Text = "Eraser";
        }
        else if (ToolSelect.IsChecked == true)
        {
            MainInkCanvas.EditingMode = InkCanvasEditingMode.Select;
            MainInkCanvas.Cursor = Cursors.Arrow;
            StatusModeText.Text = "Lasso select";
        }
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
        ColorWheelControl.SetCurrentColor(ToolHighlighter.IsChecked == true ? _highlighterColor : _penColor);
        ColorWheelPopup.IsOpen = !ColorWheelPopup.IsOpen;
    }

    private void ApplyChosenColor(Color color)
    {
        if (ToolHighlighter.IsChecked == true)
        {
            _highlighterColor = color;
        }
        else
        {
            _penColor = color;
            if (ToolEraser.IsChecked == true || ToolSelect.IsChecked == true)
            {
                ToolPen.IsChecked = true;
            }
        }

        ApplyDrawSubTool();
    }

    private void ComboWriteLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboWriteLanguage?.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<HandwritingLanguage>(item.Tag?.ToString(), out var lang))
        {
            _handwritingLanguage = lang;
            if (ModeDraw.IsChecked == true) ApplyDrawSubTool();
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
            _strokeThickness = thickness;
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

    private void MainInkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_isApplyingHistory) return;

        if (ToolWriteText.IsChecked == true)
        {
            _pendingHandwritingStrokes.Add(e.Stroke);
            _handwritingTimer.Stop();
            _handwritingTimer.Start();
            StatusModeText.Text = $"Recognizing ({_handwritingLanguage})...";
            return;
        }

        if (ToolPen.IsChecked == true && ToggleShapeAssist.IsChecked == true)
        {
            var result = ShapeRecognizer.Analyze(e.Stroke);
            if (result.IsRecognized && result.CleanedStroke != null)
            {
                // Record the raw stroke first so a single Ctrl+Z reverts to the original freehand stroke.
                RecordInkState();

                _isApplyingHistory = true;
                MainInkCanvas.Strokes.Remove(e.Stroke);
                MainInkCanvas.Strokes.Add(result.CleanedStroke);
                _isApplyingHistory = false;

                RecordInkState();
                _redoStack.Clear();
                SaveCurrentPage();
                StatusModeText.Text = $"Snapped to {result.Kind.ToString().ToLowerInvariant()} (Ctrl+Z to revert)";
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

        List<string> recognizedLines;
        try
        {
            recognizedLines = await _handwritingService.RecognizeLinesAsync(strokesToProcess, _handwritingLanguage);
        }
        catch (Exception ex)
        {
            StatusModeText.Text = $"Recognition failed: {ex.Message}";
            return;
        }

        if (recognizedLines.Count == 0)
        {
            StatusModeText.Text = "Could not recognize handwriting; strokes kept on page.";
            return;
        }

        InsertRecognizedLinesIntoDocument(recognizedLines, combinedBounds);

        _isApplyingHistory = true;
        foreach (var s in strokesToProcess)
        {
            MainInkCanvas.Strokes.Remove(s);
        }
        _isApplyingHistory = false;

        RecordInkState();
        SaveCurrentPage();
        StatusModeText.Text = $"Converted ({_handwritingLanguage}): {string.Join(" / ", recognizedLines)}";
    }

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
                    cursor = cursor.InsertParagraphBreak();
                }

                cursor.InsertTextInRun(textToInsert);
                var insertedStart = cursor;
                var insertedEnd = cursor.GetPositionAtOffset(textToInsert.Length, LogicalDirection.Forward) ?? cursor;

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
        if (_isApplyingHistory) return;
        RecordInkState();
        _redoStack.Clear();
        SaveCurrentPage();
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
        if (_undoStack.Count <= 1) return;

        _isApplyingHistory = true;
        _redoStack.Push(_undoStack.Pop());

        MainInkCanvas.Strokes.Clear();
        foreach (var s in _undoStack.Peek())
        {
            MainInkCanvas.Strokes.Add(s.Clone());
        }
        _isApplyingHistory = false;
        SaveCurrentPage();
    }

    private void RedoInk()
    {
        if (_redoStack.Count == 0) return;

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
        var result = MessageBox.Show(this, "Clear all ink strokes on this page?", "Clear ink", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            Title = "Export page",
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"{_currentPageItem?.Title ?? "YogaNote"}.png"
        };

        if (saveDlg.ShowDialog(this) == true)
        {
            try
            {
                NoteStorageService.ExportToPng(PageContentGrid, saveDlg.FileName);
                StatusModeText.Text = $"Exported to {saveDlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed: {ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

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
        if (sender is not Button btn || btn.Tag is not string pageId || _currentSection == null) return;

        var pageToDelete = _currentSection.Pages.FirstOrDefault(p => p.Id == pageId);
        if (pageToDelete == null) return;

        var result = MessageBox.Show(this, $"Delete \"{pageToDelete.Title}\"?", "Delete page", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

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

    private void BtnNewNotebook_Click(object sender, RoutedEventArgs e)
    {
        _modalMode = ModalMode.NewNotebook;
        ModalTitle.Text = "New notebook";
        ModalPrompt.Text = "Notebook name:";
        ModalInput.Text = "My Notes";
        ModalOverlay.Visibility = Visibility.Visible;
        ModalInput.Focus();
        ModalInput.SelectAll();
    }

    private void BtnNewSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNotebook == null) return;
        _modalMode = ModalMode.NewSection;
        ModalTitle.Text = "New section";
        ModalPrompt.Text = "Section name:";
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

        if (_modalMode == ModalMode.NewNotebook)
        {
            var newNotebook = new Notebook
            {
                Name = input,
                Sections = new List<Section>
                {
                    new()
                    {
                        Name = "General",
                        ColorHex = "#7719AA",
                        Pages = new List<PageItem> { new() { Title = "Untitled Page", CreatedAt = DateTime.Now } }
                    }
                }
            };

            _notebooks.Add(newNotebook);
            ComboNotebooks.Items.Refresh();
            ComboNotebooks.SelectedItem = newNotebook;
            _storageService.SaveNotebooks(_notebooks);
        }
        else if (_modalMode == ModalMode.NewSection && _currentNotebook != null)
        {
            var sectionColors = new[] { "#7719AA", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#EC4899" };
            var newSection = new Section
            {
                Name = input,
                ColorHex = sectionColors[_currentNotebook.Sections.Count % sectionColors.Length],
                Pages = new List<PageItem> { new() { Title = "Untitled Page", CreatedAt = DateTime.Now } }
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
        SaveWindowBounds();
        base.OnClosing(e);
    }
}
