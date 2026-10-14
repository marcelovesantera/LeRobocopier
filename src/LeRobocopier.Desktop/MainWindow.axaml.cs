using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Settings_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.Settings);

    private void NewProject_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.NewProject);

    private void Projects_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.Projects);

    private void Create_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Create();

    private void Open_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: Project project })
            (DataContext as MainWindowViewModel)?.Open(project);
    }

    private void Remove_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SelectedItem item })
            (DataContext as MainWindowViewModel)?.RemoveItem(item);
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs args) =>
        await SelectItems(ItemType.File);

    private async void AddFolders_Click(object? sender, RoutedEventArgs args) =>
        await SelectItems(ItemType.Folder);

    private async Task SelectItems(ItemType type)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.Selected is not { } project) return;
        try
        {
            if (type == ItemType.File && !StorageProvider.CanOpen || type == ItemType.Folder && !StorageProvider.CanPickFolder)
            {
                viewModel.ReportError("The picker is unavailable in this environment.");
                return;
            }
            IReadOnlyList<IStorageItem> chosen = type == ItemType.File
                ? await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files", AllowMultiple = true })
                : await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add folders", AllowMultiple = true });
            try
            {
                if (chosen.Count == 0) return;
                var items = chosen.Select(item => new SelectedItem(type,
                    item.TryGetLocalPath() ?? throw new ArgumentException("Select items with full local paths."))).ToArray();
                // The ID captured before the dialog prevents adding items to a different project.
                viewModel.AddItems(project.Id, items);
            }
            finally
            {
                foreach (var item in chosen) item.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            viewModel.ReportError(error.Message);
        }
    }
}
