using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RAWimp.App;

// The About dialog: what this is, which build you're running, and where the source and licence live.
public sealed partial class MainWindow
{
    private const string ProjectUrl = "https://github.com/emrox/rawimp";

    /// The build's own version. InformationalVersion carries what the csproj (or CI) set; it can end
    /// in "+<commit sha>", which is noise here.
    private static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(v)) return "unknown";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    private async void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        var dim = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock
        {
            Text = "RAWimp",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        heading.Children.Add(new TextBlock { Text = "Photo browser, culler and importer", Foreground = dim });
        heading.Children.Add(new TextBlock { Text = "Version " + AppVersion, FontSize = 12, Foreground = dim });

        var logo = new Image
        {
            Width = 64,
            Height = 64,
            Source = new BitmapImage(new Uri("ms-appx:///logo.png")),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 380, MaxWidth = 420 };
        panel.Children.Add(Row(logo, heading));
        panel.Children.Add(new TextBlock
        {
            Text = "Free software under the GNU General Public License v3.0. The name and logo are "
                 + "not covered by that licence and remain the author's.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = dim,
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = "Source code and licence on GitHub",
            NavigateUri = new Uri(ProjectUrl),
            Padding = new Thickness(0, 4, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright © 2026 Stefan Bauckmeier",
            FontSize = 12,
            Foreground = dim,
        });

        await ShowModalAsync(new ContentDialog
        {
            Title = "About",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        });
    }
}
