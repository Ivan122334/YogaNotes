using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YogaNotes.Models;

namespace YogaNotes.Controls;

public partial class FloatingNoteControl : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragging = false;

    public NoteTextBox Model { get; private set; }

    public event EventHandler<FloatingNoteControl>? DeleteRequested;
    public event EventHandler? ContentChanged;
    public event EventHandler? PositionChanged;

    public FloatingNoteControl(NoteTextBox model)
    {
        InitializeComponent();
        Model = model;

        Width = model.Width > 0 ? model.Width : 260;
        Height = model.Height > 0 ? model.Height : 120;
        NoteTextBoxControl.Text = model.Text ?? string.Empty;
        NoteTextBoxControl.FontSize = model.FontSize > 0 ? model.FontSize : 14;

        InkCanvas.SetLeft(this, model.X);
        InkCanvas.SetTop(this, model.Y);

        UpdateVisualState(false);
    }

    public void FocusEditor()
    {
        NoteTextBoxControl.Focus();
        NoteTextBoxControl.CaretIndex = NoteTextBoxControl.Text.Length;
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(Parent as UIElement);
        HeaderBar.CaptureMouse();
        UpdateVisualState(true);
        e.Handled = true;
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && Parent is UIElement parentContainer)
        {
            var currentPos = e.GetPosition(parentContainer);
            var deltaX = currentPos.X - _dragStartPoint.X;
            var deltaY = currentPos.Y - _dragStartPoint.Y;

            var currentLeft = InkCanvas.GetLeft(this);
            var currentTop = InkCanvas.GetTop(this);

            if (double.IsNaN(currentLeft)) currentLeft = 0;
            if (double.IsNaN(currentTop)) currentTop = 0;

            var newLeft = Math.Max(0, currentLeft + deltaX);
            var newTop = Math.Max(0, currentTop + deltaY);

            InkCanvas.SetLeft(this, newLeft);
            InkCanvas.SetTop(this, newTop);

            Model.X = newLeft;
            Model.Y = newTop;

            _dragStartPoint = currentPos;
            PositionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            HeaderBar.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newWidth = Math.Max(MinWidth, Width + e.HorizontalChange);
        var newHeight = Math.Max(MinHeight, Height + e.VerticalChange);

        Width = newWidth;
        Height = newHeight;

        Model.Width = newWidth;
        Model.Height = newHeight;

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, this);
    }

    private void NoteTextBoxControl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Model != null)
        {
            Model.Text = NoteTextBoxControl.Text;
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NoteTextBoxControl_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateVisualState(true);
    }

    private void NoteTextBoxControl_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateVisualState(false);
    }

    private void UpdateVisualState(bool isActive)
    {
        if (isActive)
        {
            OuterBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x19, 0xAA)); // Purple accent
            HeaderBar.Visibility = Visibility.Visible;
            ResizeThumb.Visibility = Visibility.Visible;
            OuterBorder.Background = new SolidColorBrush(Colors.White);
        }
        else
        {
            OuterBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8)); // Soft subtle border
            HeaderBar.Visibility = Visibility.Visible;
            ResizeThumb.Visibility = Visibility.Visible;
            OuterBorder.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        }
    }
}
