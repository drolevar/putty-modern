using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PuTTY.App;

/// <summary>
/// The handle between two column headers: a thin line with a wider area to take hold of, the resize cursor over it, and a drag
/// that asks for a new width for the column on its left. Standard Fluent controls have no resizable column, and the Connection
/// log's rows are a ListView's, so the header is built from these.
/// </summary>
/// <remarks>
/// A subclass of a WinUI control, so <c>partial</c> for NativeAOT (CLAUDE.md). The pointer is followed in the coordinates of
/// <c>reference</c>, an element that does not move while the column is resized: the grip itself moves with the column's edge, and
/// reading the pointer against it would feed the movement back into the next one.
/// </remarks>
internal sealed partial class ColumnGrip : Grid
{
    private readonly UIElement reference;
    private readonly Func<double> getWidth;
    private readonly Action<double> setWidth;
    private bool dragging;
    private double startX;
    private double startWidth;

    public ColumnGrip(UIElement reference, Func<double> getWidth, Action<double> setWidth)
    {
        this.reference = reference;
        this.getWidth = getWidth;
        this.setWidth = setWidth;

        Width = 12;
        Background = new SolidColorBrush(Colors.Transparent);      // a Grid with no background does not take the pointer
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
        if (Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out object? brush) && brush is Brush stroke)
        {
            line.Background = stroke;
        }

        Children.Add(line);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => dragging = false;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CapturePointer(e.Pointer))
        {
            return;
        }

        dragging = true;
        startX = e.GetCurrentPoint(reference).Position.X;
        startWidth = getWidth();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragging)
        {
            setWidth(startWidth + e.GetCurrentPoint(reference).Position.X - startX);
            e.Handled = true;
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }
}
