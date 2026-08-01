using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Path = Microsoft.UI.Xaml.Shapes.Path;   // not System.IO.Path

namespace RAWimp.App;

/// Lucide (https://lucide.dev) icons, ISC licensed — see THIRD-PARTY-NOTICES.md.
///
/// Not PathIcon: that *fills* its geometry, and Lucide draws with strokes on a 24x24 grid, so filled
/// versions come out as blobs. These render as stroked Paths with the round caps and 2px weight the
/// set is designed around, scaled by a Viewbox.
///
/// One Path per SVG element rather than one concatenated string: several icons have elements whose
/// data starts with a relative moveto ("m6 6"), which is relative to the origin only while the
/// element stands alone. Joining them end to end silently displaces every stroke after the first.
public sealed class LucideIcon : ContentControl
{
    private static readonly Dictionary<string, string[]> Paths = new()
    {
        ["arrow-up-down"] = ["m21 16-4 4-4-4", "M17 20V4", "m3 8 4-4 4 4", "M7 4v16"],
        ["ban"] = ["M4.929 4.929 19.07 19.071", "M 2,12 a 10,10 0 1 0 20,0 a 10,10 0 1 0 -20,0"],
        ["camera"] = ["M13.997 4a2 2 0 0 1 1.76 1.05l.486.9A2 2 0 0 0 18.003 7H20a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h1.997a2 2 0 0 0 1.759-1.048l.489-.904A2 2 0 0 1 10.004 4z", "M 9,13 a 3,3 0 1 0 6,0 a 3,3 0 1 0 -6,0"],
        ["folder-open"] = ["m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2"],
        ["folder"] = ["M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"],
        ["funnel"] = ["M10 20a1 1 0 0 0 .553.895l2 1A1 1 0 0 0 14 21v-7a2 2 0 0 1 .517-1.341L21.74 4.67A1 1 0 0 0 21 3H3a1 1 0 0 0-.742 1.67l7.225 7.989A2 2 0 0 1 10 14z"],
        ["import"] = ["M12 3v12", "m8 11 4 4 4-4", "M8 5H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4"],
        ["info"] = ["M12 16v-4", "M12 8h.01", "M 2,12 a 10,10 0 1 0 20,0 a 10,10 0 1 0 -20,0"],
        ["menu"] = ["M4 5h16", "M4 12h16", "M4 19h16"],
        ["rotate-ccw"] = ["M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5"],
        ["settings"] = ["M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915", "M 9,12 a 3,3 0 1 0 6,0 a 3,3 0 1 0 -6,0"],
        ["star"] = ["M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z"],
        ["trash-2"] = ["M10 11v6", "M14 11v6", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M3 6h18", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"],
        ["x"] = ["M18 6 6 18", "m6 6 12 12"],
    };

    /// Button content of an icon beside a label, for the dialogs that are built in code.
    public static StackPanel Label(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Children =
        {
            new LucideIcon { Glyph = glyph },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    private string _glyph = "";
    private double _size = 16;
    private bool _filled;
    private Brush? _tint;

    public LucideIcon()
    {
        IsTabStop = false;
        IsHitTestVisible = false;   // never steal the click from the button hosting it
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;

        // Foreground is an inherited property, so a plain Path child would ignore it while every
        // sibling TextBlock follows the button's hover/pressed/disabled colours. Track it instead.
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => QueueRebuild());
    }

    /// Lucide icon name, e.g. "folder-open". Unknown names render nothing rather than throwing —
    /// a missing icon must not take the window down.
    public string Glyph
    {
        get => _glyph;
        set { _glyph = value; QueueRebuild(); }
    }

    /// Edge length in pixels; the 24x24 artwork is scaled to fit.
    public double Size
    {
        get => _size;
        set { _size = value; QueueRebuild(); }
    }

    /// Fill the shape as well as stroking it — how Lucide renders an "on" star.
    public bool Filled
    {
        get => _filled;
        set { _filled = value; QueueRebuild(); }
    }

    /// Overrides the inherited Foreground, for the few places wanting a fixed colour (rating gold).
    public Brush? Tint
    {
        get => _tint;
        set { _tint = value; QueueRebuild(); }
    }

    private bool _rebuildQueued;

    /// Setting four properties from XAML would otherwise rebuild the geometry four times, once per
    /// assignment — on a virtualised grid that is per cell, per scroll. Collapse them into one.
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        DispatcherQueue.TryEnqueue(() => { _rebuildQueued = false; Rebuild(); });
    }

    private void Rebuild()
    {
        if (!Paths.TryGetValue(_glyph, out var data))
        {
            Content = null;
            if (_glyph.Length > 0) Diag.Log("unknown lucide icon: " + _glyph);
            return;
        }

        var brush = _tint ?? Foreground ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var canvas = new Canvas { Width = 24, Height = 24 };

        foreach (var d in data)
            canvas.Children.Add(new Path
            {
                Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), d),
                Stroke = brush,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = _filled ? brush : null,
            });

        Content = new Viewbox { Width = _size, Height = _size, Child = canvas };
    }
}
