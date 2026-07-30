using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RAWimp.App;

// Button intent, in the platform's own vocabulary: WinUI calls the default action "Accent" and the
// error state "Critical", so these wrap those rather than inventing a parallel naming scheme.
//
//   button.AsPrimary()   the action the dialog is for (same look as a ContentDialog's primary button)
//   button.AsDanger()    destructive: delete, stop, discard
static class Theme
{
    /// The default/confirming action.
    public static Button AsPrimary(this Button button)
    {
        if (Application.Current.Resources["AccentButtonStyle"] is Style accent) button.Style = accent;
        return button;
    }

    /// Destructive action — same shape as the accent button, in critical red.
    ///
    /// The accent style swaps its background per visual state, so setting Background alone would be
    /// discarded the moment the pointer moved over it. Overriding the brushes in the button's own
    /// resources makes every state resolve to the danger colour instead.
    public static Button AsDanger(this Button button)
    {
        button.AsPrimary();
        var res = Application.Current.Resources;
        button.Resources["AccentButtonBackground"] = res["DangerButtonBackground"];
        button.Resources["AccentButtonBackgroundPointerOver"] = res["DangerButtonBackgroundPointerOver"];
        button.Resources["AccentButtonBackgroundPressed"] = res["DangerButtonBackgroundPressed"];
        return button;
    }
}
