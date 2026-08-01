using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace RAWimp.App;

// Rubber-band ("marquee") selection: drag a box across empty space in the grid to select everything
// it touches, the way Explorer does. WinUI's GridView has no such thing, so it is drawn and
// hit-tested here.
//
// Ctrl+click, Shift+click and Ctrl+A come free from SelectionMode="Extended" on the GridView.
public sealed partial class MainWindow
{
    private bool _marqueeActive;
    private bool _marqueeDragged;
    private Point _marqueeStart;
    private List<PhotoItem> _selectionBeforeMarquee = new();

    private void HookMarquee()
    {
        // handledEventsToo: the GridView's own scroll viewer marks pointer events handled, so a
        // plain event subscription would never see the drag begin.
        ThumbGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGridPointerPressed), true);
        ThumbGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnGridPointerMoved), true);
        ThumbGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnGridPointerReleased), true);
        ThumbGrid.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnGridPointerReleased), true);
        ThumbGrid.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnGridPointerReleased), true);
    }

    private void OnGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Pressing on a photo must still start a drag, so only empty space begins a marquee.
        if (FindDataContext<PhotoItem>(e.OriginalSource) is not null) return;

        var point = e.GetCurrentPoint(ThumbGrid);
        if (!point.Properties.IsLeftButtonPressed) return;

        _marqueeStart = point.Position;
        _marqueeActive = true;
        _marqueeDragged = false;

        // Holding Ctrl or Shift adds to what is already selected, rather than replacing it.
        _selectionBeforeMarquee = Extending() ? ThumbGrid.SelectedItems.OfType<PhotoItem>().ToList() : new();

        ThumbGrid.CapturePointer(e.Pointer);
    }

    private void OnGridPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;

        var current = e.GetCurrentPoint(ThumbGrid).Position;
        var box = Normalise(_marqueeStart, current);

        // A couple of stray pixels shouldn't wipe the selection; wait for a real drag.
        if (box.Width < 4 && box.Height < 4) return;

        _marqueeDragged = true;
        Marquee.Visibility = Visibility.Visible;
        Marquee.Margin = new Thickness(box.Left, box.Top, 0, 0);
        Marquee.Width = box.Width;
        Marquee.Height = box.Height;

        SelectWithin(box);
        e.Handled = true;
    }

    private void OnGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        Marquee.Visibility = Visibility.Collapsed;
        ThumbGrid.ReleasePointerCapture(e.Pointer);

        // A click on empty space with no drag clears the selection, as Explorer does. The GridView
        // would normally do this itself, but capturing the pointer takes that away from it.
        if (!_marqueeDragged && !Extending()) ThumbGrid.SelectedItems.Clear();
    }

    /// Select every item the box touches. Only realised containers can be measured, which is fine:
    /// the box can only cover what is on screen.
    private void SelectWithin(Rect box)
    {
        var hits = new List<PhotoItem>();
        for (var i = 0; i < _current.Count; i++)
        {
            if (ThumbGrid.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var bounds = container
                .TransformToVisual(ThumbGrid)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (Intersects(bounds, box)) hits.Add(_current[i]);
        }

        var wanted = _selectionBeforeMarquee.Union(hits).ToList();

        // Update by difference — clearing and refilling would restart the selection animation on
        // every pointer move and flicker badly.
        var selected = ThumbGrid.SelectedItems;
        for (var i = selected.Count - 1; i >= 0; i--)
            if (selected[i] is PhotoItem item && !wanted.Contains(item)) selected.RemoveAt(i);
        foreach (var item in wanted)
            if (!selected.Contains(item)) selected.Add(item);
    }

    private static bool Extending() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift);

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static Rect Normalise(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
