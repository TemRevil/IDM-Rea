using Microsoft.UI.Xaml.Controls;
namespace IDMRea.App.Views;

public sealed partial class DownloadsPage : Page
{
    private AppState State => MainWindow.Instance.State;

    public DownloadsPage()
    {
        InitializeComponent();
        List.ItemsSource = State.Visible;
    }

    public void Select(Guid id)
    {
        if (State.Find(id) is not { } row) return;
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }
}
