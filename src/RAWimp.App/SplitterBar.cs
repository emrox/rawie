using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace RAWimp.App;

// A thin draggable divider. WinUI ships no GridSplitter, and pulling in a toolkit package for one
// control isn't worth it — this is just pointer capture plus a resize cursor.
//
// Derives from Grid: Border is sealed, and ContentControl doesn't paint its Background, so it was
// neither visible nor hit-testable. A Panel fills its Background and takes pointer input.
public sealed partial class SplitterBar : Grid
{
    public SplitterBar()
    {
        IsTabStop = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
