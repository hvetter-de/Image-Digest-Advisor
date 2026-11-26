using ImageDigestAdvisor.App.ViewModels;
using ImageDigestAdvisor.Core.Analysis;
using ImageDigestAdvisor.Core.Security;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ImageDigestAdvisor.App.Views;

/// <summary>Two images of a tag's history side by side, with what changed between them.</summary>
public sealed partial class CompareDialog : ContentDialog
{
    public CompareDialog(HistoryEntryViewModel older, HistoryEntryViewModel newer)
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 1100d;
        Resources["ContentDialogMaxHeight"] = 900d;
        Title = $"{older.Title} → {newer.Title}";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;

        var a = older.Details!;
        var b = newer.Details!;

        var panel = new StackPanel { Spacing = 14, MinWidth = 760 };
        panel.Children.Add(new TextBlock { Text = Summary(a, b), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (CveSummary(older.Card?.Report, newer.Card?.Report) is { } cves)
        {
            panel.Children.Add(new TextBlock { Text = cves, TextWrapping = TextWrapping.Wrap });
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow(grid, string.Empty, older.Title, newer.Title, header: true);
        AddRow(grid, "Created", Date(a.Created), Date(b.Created));
        AddRow(grid, "Size (compressed)", ImageCardViewModel.FormatSize(a.CompressedSize), ImageCardViewModel.FormatSize(b.CompressedSize) + $"  ({Delta(b.CompressedSize - a.CompressedSize)})");
        AddRow(grid, "Layers", a.LayerCount.ToString(), b.LayerCount.ToString());
        AddRow(grid, "Platform", a.Platform.ToString(), b.Platform.ToString());

        var names = a.SoftwareVersions.Select(v => v.Name).Concat(b.SoftwareVersions.Select(v => v.Name)).Distinct().ToList();
        foreach (var name in names)
        {
            AddRow(grid, new SoftwareVersion(name, string.Empty).DisplayName,
                a.SoftwareVersions.FirstOrDefault(v => v.Name == name)?.Value ?? "–",
                b.SoftwareVersions.FirstOrDefault(v => v.Name == name)?.Value ?? "–", mono: true);
        }

        foreach (var (label, key) in new[]
                 {
                     ("Version label", "org.opencontainers.image.version"),
                     ("Base image", "org.opencontainers.image.base.name"),
                     ("Revision", "org.opencontainers.image.revision"),
                 })
        {
            if (a.Label(key) is null && b.Label(key) is null) continue;
            AddRow(grid, label, a.Label(key) ?? "–", b.Label(key) ?? "–");
        }

        AddRow(grid, "Tags", Tags(older), Tags(newer));
        AddRow(grid, "Digest", older.Entry.Digest, newer.Entry.Digest, mono: true);

        panel.Children.Add(grid);
        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };
    }

    private static string Summary(ImageDetails a, ImageDetails b)
    {
        var parts = new List<string>();
        if (a.Created is { } ca && b.Created is { } cb)
        {
            var days = (int)Math.Round((cb - ca).TotalDays);
            parts.Add(days == 0 ? "Built on the same day" : $"Built {days} days apart");
        }
        parts.Add($"size {Delta(b.CompressedSize - a.CompressedSize)}");
        parts.Add($"{b.LayerCount - a.LayerCount:+0;−0;±0} layers");

        var changes = b.SoftwareChangesSince(a);
        var text = string.Join(", ", parts) + ".";
        if (changes.Count > 0) text += "\nSoftware changes: " + string.Join(", ", changes) + ".";
        else if (a.SoftwareVersions.Count > 0) text += "\nSame declared software versions – most likely base image or OS package updates.";
        return text;
    }

    private static string? CveSummary(VulnerabilityReport? older, VulnerabilityReport? newer)
    {
        if (older is null || newer is null) return "To compare known vulnerabilities, run the CVE check on both images (open each row).";
        if (!older.IsCompleted || !newer.IsCompleted)
        {
            return older.Status == VulnerabilityScanStatus.NoSbom || newer.Status == VulnerabilityScanStatus.NoSbom
                ? "Known vulnerabilities can't be compared: the registry publishes no SBOM for at least one of the images."
                : "Known vulnerabilities can't be compared: the CVE check failed for at least one of the images.";
        }
        var fixedCount = newer.FixedComparedTo(older).Count;
        var newCount = newer.NewComparedTo(older).Count;
        return $"Known vulnerabilities: {older.FixableCount} → {newer.FixableCount} with a fix available. The newer image fixes {fixedCount} and adds {newCount}.";
    }

    private static string Date(DateTimeOffset? d) => d is { } v ? ImageCardViewModel.FormatTime(v) : "unknown";

    private static string Delta(long bytes) => bytes switch
    {
        0 => "±0",
        > 0 => "+" + ImageCardViewModel.FormatSize(bytes),
        _ => "−" + ImageCardViewModel.FormatSize(-bytes),
    };

    private static string Tags(HistoryEntryViewModel e) => e.Entry.Tags.Count > 0 ? string.Join(", ", e.Entry.Tags) : "(none)";

    private static void AddRow(Grid grid, string name, string left, string right, bool header = false, bool mono = false)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var differs = !header && left != right;

        var font = mono ? (FontFamily)Application.Current.Resources["MonoFont"] : null;
        for (var col = 0; col < 3; col++)
        {
            var text = new TextBlock
            {
                Text = col switch { 0 => name, 1 => left, _ => right },
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            if (col == 0)
            {
                text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
                text.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }
            else
            {
                if (font is not null) text.FontFamily = font;
                if (header || (differs && col == 2)) text.FontWeight = FontWeights.SemiBold;
            }
            Grid.SetRow(text, row);
            Grid.SetColumn(text, col);
            grid.Children.Add(text);
        }
    }
}
