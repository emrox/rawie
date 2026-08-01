using Microsoft.UI.Xaml.Media;

namespace RAWimp.App;

/// One icon in a rating badge — a star, or the ban mark for a rejected photo.
///
/// Carries its own brush rather than inheriting Foreground: the badge sits on a dark overlay in the
/// grid, where the surrounding foreground says nothing about whether a pip should be gold or red.
public sealed class RatingPip(string glyph, bool filled, Brush brush)
{
    public string Glyph { get; } = glyph;
    public bool Filled { get; } = filled;
    public Brush Brush { get; } = brush;
}
