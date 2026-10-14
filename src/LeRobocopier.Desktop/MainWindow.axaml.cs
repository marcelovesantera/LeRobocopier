using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, args) => args.Cancel = (DataContext as MainWindowViewModel)?.IsBusy == true;
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }

    private async void Copy_Click(object? sender, RoutedEventArgs args) => await RunOperation(false);

    private async void Restore_Click(object? sender, RoutedEventArgs args) => await RunOperation(true);

    private async void Move_Click(object? sender, RoutedEventArgs args) => await RunOperation(false, true);

    private async Task RunOperation(bool restore, bool move = false)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCopy) return;
        var operation = restore ? "Restore" : move ? "Move" : "Copy";
        var project = viewModel.Selected!;
        viewModel.IsBusy = true;
        var (modal, details, bar, summary, choices, content) = CreateOperationDialog(operation, project.Name);
        var running = true;
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = running;
        var modalClosed = modal.ShowDialog(this);
        CopyPlan? plan = null;
        var executed = false;
        var showProgress = true;
        var progress = new Progress<OperationProgress>(update =>
        {
            if (!showProgress) return;
            details.Text = $"{update.Stage}: {update.Path}";
            bar.IsIndeterminate = update.Total == 0;
            bar.Value = update.Total == 0 ? 0 : 100d * update.Completed / update.Total;
        });
        try
        {
            var preparation = await Task.Run(() => restore
                ? viewModel.PrepareRestore(project.Id, progress) : move
                    ? viewModel.PrepareMove(project.Id, progress) : viewModel.PrepareCopy(project.Id, progress));
            showProgress = false;
            if (preparation.Failure is { } failure)
            {
                details.Text = "Preparation failed. No operation files were changed.";
                summary.Text = $"{failure.Stage}: {failure.Path}: {failure.Message}";
            }
            else
            {
                plan = preparation.Plan!;
                var skip = new CheckBox { Content = $"Do not ask again for {operation}" };
                if (restore ? viewModel.ConfirmRestore : move ? viewModel.ConfirmMove : viewModel.ConfirmCopy)
                {
                    details.Text = $"{operation} {plan.Entries.Count} file(s) and folder(s) for {project.Name}? " +
                        (restore ? "Corresponding originals will be replaced. Backup and extra original files will be preserved."
                            : move ? "Originals will be removed only after all copies are verified. Extra backup files will be preserved."
                            : "Originals and extra backup files will be preserved.");
                    bar.IsIndeterminate = false;
                    summary.Text = string.Join(Environment.NewLine, plan.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}"));
                    summary.Text += "\nDestinations:\n" + string.Join(Environment.NewLine,
                        plan.Entries.Select(entry => entry.Destination));
                    if (move)
                        summary.Text += "\nOriginals to remove after verification:\n" + string.Join(Environment.NewLine,
                            plan.Entries.Select(entry => entry.Source));
                    if (restore)
                        summary.Text += "\nFiles to overwrite:\n" + (plan.Overwrites!.Count == 0 ? "None."
                            : string.Join(Environment.NewLine, plan.Overwrites));
                    content.Children.Insert(4, skip);
                    var decision = new TaskCompletionSource<bool>();
                    var proceed = new Button { Content = operation };
                    var decline = new Button { Content = "Back" };
                    proceed.Click += (_, _) => decision.TrySetResult(true);
                    decline.Click += (_, _) => decision.TrySetResult(false);
                    choices.Children.Add(proceed);
                    choices.Children.Add(decline);
                    if (!await decision.Task)
                    {
                        viewModel.AbandonCopy(plan);
                        plan = null;
                        running = false;
                        modal.Close();
                        return;
                    }
                    choices.Children.Clear();
                    content.Children.Remove(skip);
                }
                summary.Text = "";
                var disableConfirmation = skip.IsChecked == true;
                executed = true;
                showProgress = true;
                var result = await Task.Run(() => restore
                    ? viewModel.Restore(plan!, disableConfirmation, progress) : move
                        ? viewModel.Move(plan!, disableConfirmation, progress) : viewModel.Copy(plan!, disableConfirmation, progress));
                showProgress = false;
                details.Text = result.Failure is not null ? move && result.Failure.Stage == OperationStage.Removing
                    ? result.RemovedPaths.Count > 0
                        ? "Move partially failed during removal. Removed originals were not rolled back. Check reported errors before using backup copies."
                        : "Move failed before removing originals. Check reported errors before using backup copies."
                    : $"{operation} failed. Files already written were not rolled back."
                    : result.WrittenPaths.Count == 0 ? "No items processed." : result.Warnings.Count > 0
                        ? $"{operation} completed with warnings." : $"{operation} completed and verified.";
                summary.Text = $"Written paths (including potentially incomplete files): {result.WrittenPaths.Count}\nVerified files: {result.VerifiedFiles.Count}\n" +
                    string.Join(Environment.NewLine, result.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}")) +
                    (result.Failure is null ? "" : $"\nFailure — {result.Failure.Stage}: {result.Failure.Path}: {result.Failure.Message}") +
                    (result.WrittenPaths.Count == 0 ? "" : "\nWritten paths:\n" + string.Join(Environment.NewLine, result.WrittenPaths));
                if (move)
                    summary.Text += $"\nRemoved original paths: {result.RemovedPaths.Count}\n" +
                        (result.RemovedPaths.Count == 0 ? "None." : string.Join(Environment.NewLine, result.RemovedPaths)) +
                        $"\nOriginal paths not removed: {result.RemainingPaths.Count}\n" +
                        (result.RemainingPaths.Count == 0 ? "None." : string.Join(Environment.NewLine, result.RemainingPaths)) +
                        (result.VerifiedFiles.Count == 0 ? "" : "\nBackup files that passed global verification before removal:\n" +
                            string.Join(Environment.NewLine, result.VerifiedFiles));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            details.Text = $"{operation} could not finish.";
            summary.Text = error.Message;
        }
        finally
        {
            showProgress = false;
            if (plan is not null && !executed) viewModel.AbandonCopy(plan);
            running = false;
            bar.IsIndeterminate = false;
            viewModel.IsBusy = false;
            viewModel.RefreshConfirmation();
        }
        if (modal.IsVisible)
        {
            var close = new Button { Content = "Close" };
            close.Click += (_, _) => modal.Close();
            choices.Children.Clear();
            choices.Children.Add(close);
            await modalClosed;
        }
    }

    private static (Window Window, TextBlock Details, ProgressBar Bar, TextBlock Summary,
        StackPanel Choices, StackPanel Content) CreateOperationDialog(string operation, string projectName)
    {
        var details = new TextBlock { Text = "Preparing the inventory…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        var summary = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var content = new StackPanel { Spacing = 18, Margin = new Avalonia.Thickness(24) };
        content.Children.Add(new TextBlock { Text = $"{operation} — {projectName}", FontSize = 22 });
        content.Children.Add(details);
        content.Children.Add(bar);
        content.Children.Add(new ScrollViewer { Content = summary, MaxHeight = 240 });
        content.Children.Add(choices);
        var modal = new Window
        {
            Title = operation, Width = 640, Height = 500, MinWidth = 480, MinHeight = 380,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
        };
        return (modal, details, bar, summary, choices, content);
    }

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
