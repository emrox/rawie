namespace RAWimp.App;

/// One entry in the filter or sort dropdown.
///
/// A class rather than a record: x:Bind in a DataTemplate needs a type it can bind against, and the
/// dropdowns are fixed lists that never change after construction, so there is nothing to notify.
public sealed class ViewOption(string tag, string label, string glyph, bool filled)
{
    /// What ApplyView switches on ("all", "3", "rej", "rating", …).
    public string Tag { get; } = tag;

    /// Shown beside the icon, and reused as the burger submenu's item text.
    public string Label { get; } = label;

    /// Lucide icon name.
    public string Glyph { get; } = glyph;

    /// Solid star for a rating threshold, outline for "unrated".
    public bool Filled { get; } = filled;
}
