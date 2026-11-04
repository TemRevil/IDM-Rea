using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
namespace IDMRea.App.Views;

/// <summary>One thin bar per part, each as wide as its share of the file and filled by how much of it has arrived.</summary>
public sealed partial class PartsMap : Grid
{
    private string shape = "";

    public PartsMap()
    {
        Height = 6;
        ColumnSpacing = 2;
    }

    public void Show(DownloadJob job)
    {
        var parts = job.Parts is { Count: > 0 } p ? p
            : job.TotalBytes is long total && total > 0 ? [new DownloadPart(0, total - 1, Math.Min(job.ReceivedBytes, total))]
            : job.Status == DownloadStatus.Completed ? [new DownloadPart(0, 0, 1)]
            : [new DownloadPart(0, 0, 0)];
        var key = string.Join(",", parts.Select(x => x.Length));
        if (key != shape) Build(parts, key);
        for (var i = 0; i < parts.Count; i++)
        {
            var bar = (Grid)Children[i];
            var done = Math.Clamp(parts[i].Received, 0, parts[i].Length);
            bar.ColumnDefinitions[0].Width = new GridLength(Math.Max(done, 0), GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(Math.Max(parts[i].Length - done, 0), GridUnitType.Star);
        }
    }

    private void Build(IReadOnlyList<DownloadPart> parts, string key)
    {
        shape = key;
        Children.Clear(); ColumnDefinitions.Clear();
        var fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var track = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"];
        for (var i = 0; i < parts.Count; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(parts[i].Length, 1), GridUnitType.Star) });
            var bar = new Grid { CornerRadius = new CornerRadius(3), Background = track };
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.Children.Add(new Rectangle { Fill = fill });
            SetColumn(bar, i);
            Children.Add(bar);
        }
    }
}
