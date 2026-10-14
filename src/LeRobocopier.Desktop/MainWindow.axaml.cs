using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Data;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public partial class MainWindow : Window
{
    private bool isInitializing = true;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateProjectLayout();
        Opened += (_, _) => UpdateProjectLayout();
        Opened += InitializeWindow;
        Closing += (_, args) => args.Cancel = isInitializing || (DataContext as MainWindowViewModel)?.IsBusy == true;
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }

    private async void InitializeWindow(object? sender, EventArgs args)
    {
        if (!isInitializing) return;
        MainWindowViewModel? viewModel = null;
        try
        {
            var initialization = Task.Run(() => new MainWindowViewModel());
            await Task.WhenAll(initialization, Task.Delay(TimeSpan.FromSeconds(4)));
            viewModel = await initialization;
            DataContext = viewModel;
            Shell.IsVisible = true;
            InitializationScreen.IsVisible = false;
        }
        catch (Exception error)
        {
            viewModel?.Dispose();
            DataContext = null;
            InitializationMessage.Text = $"Le Robocopier could not start: {error.Message}";
            InitializationMessage.Classes.Add("error");
        }
        finally
        {
            InitializationProgress.IsVisible = false;
            isInitializing = false;
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs args) => await RunOperation(false);

    private async void Restore_Click(object? sender, RoutedEventArgs args) => await RunOperation(true);

    private async void Move_Click(object? sender, RoutedEventArgs args) => await RunOperation(false, true);

    private async void ChangeBackupRoot_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCreate) return;
        string? backupRoot = null;
        viewModel.IsBusy = true;
        try
        {
            if (!StorageProvider.CanPickFolder) throw new NotSupportedException("The folder picker is unavailable in this environment.");
            var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a new backup root", AllowMultiple = false
            });
            try
            {
                if (chosen.Count > 0)
                    backupRoot = chosen[0].TryGetLocalPath() ?? throw new ArgumentException("Select a folder with a full local path.");
            }
            finally
            {
                foreach (var folder in chosen) folder.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            viewModel.ReportError(error.Message);
        }
        finally { viewModel.IsBusy = false; }
        if (backupRoot is not null) await RunOperation(false, migrationRoot: backupRoot);
    }

    private async Task RunOperation(bool restore, bool move = false, string? migrationRoot = null)
    {
        var migration = migrationRoot is not null;
        if (DataContext is not MainWindowViewModel viewModel || !(migration ? viewModel.CanCreate : viewModel.CanCopy)) return;
        var operation = migration ? "Migrate backup root" : restore ? "Restore" : move ? "Move" : "Copy";
        var project = viewModel.Selected;
        var oldRoot = viewModel.BackupRoot;
        viewModel.IsBusy = true;
        var (modal, details, bar, summary, choices, content) = CreateOperationDialog(operation, migration ? "All projects" : project!.Name);
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
            var preparation = await Task.Run(() => migration ? viewModel.PrepareMigration(migrationRoot!, progress) : restore
                ? viewModel.PrepareRestore(project!.Id, progress) : move
                    ? viewModel.PrepareMove(project!.Id, progress) : viewModel.PrepareCopy(project!.Id, progress));
            showProgress = false;
            if (preparation.Failure is { } failure)
            {
                details.Text = "Preparation failed. No operation files were changed.";
                summary.Text = $"{failure.Stage}: {failure.Path}: {failure.Message}";
                SetOperationStatus(details, "error");
            }
            else if (preparation.Plan is null)
            {
                details.Text = "This folder is already the active backup root. No migration is needed.";
            }
            else
            {
                plan = preparation.Plan!;
                var skip = new CheckBox { Content = $"Do not ask again for {operation}" };
                if (migration || (restore ? viewModel.ConfirmRestore : move ? viewModel.ConfirmMove : viewModel.ConfirmCopy))
                {
                    details.Text = migration
                        ? $"Migrate {plan.Entries.Count} file(s) and folder(s) for all active and inactive projects from {oldRoot} to {migrationRoot}? Complete backups, including extra files, will be copied and verified before saving the new root and removing the old copies. Unrelated files in both roots will be preserved."
                        : $"{operation} {plan.Entries.Count} file(s) and folder(s) for {project!.Name}? " +
                        (restore ? "Corresponding originals will be replaced. Backup and extra original files will be preserved."
                            : move ? "Originals will be removed only after all copies are verified. Extra backup files will be preserved."
                            : "Originals and extra backup files will be preserved.");
                    bar.IsIndeterminate = false;
                    summary.Text = string.Join(Environment.NewLine, plan.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}"));
                    summary.Text += "\nDestinations:\n" + string.Join(Environment.NewLine,
                        plan.Entries.Select(entry => entry.Destination));
                    if (move || migration)
                        summary.Text += (migration ? "\nOld backup paths to remove after verification and saving:\n" : "\nOriginals to remove after verification:\n") + string.Join(Environment.NewLine,
                            plan.Entries.Select(entry => entry.Source));
                    if (restore)
                        summary.Text += "\nFiles to overwrite:\n" + (plan.Overwrites!.Count == 0 ? "None."
                            : string.Join(Environment.NewLine, plan.Overwrites));
                    if (!migration) content.Children.Insert(4, skip);
                    var decision = new TaskCompletionSource<bool>();
                    var proceed = new Button { Content = operation };
                    proceed.Classes.Add("primary");
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
                var result = await Task.Run(() => migration ? viewModel.Migrate(plan!, progress) : restore
                    ? viewModel.Restore(plan!, disableConfirmation, progress) : move
                        ? viewModel.Move(plan!, disableConfirmation, progress) : viewModel.Copy(plan!, disableConfirmation, progress));
                showProgress = false;
                details.Text = migration
                    ? result.Failure is null ? "Migration completed. The new backup root is active."
                        : result.BackupRootChanged
                            ? "The new backup root is active. Migration reported a failure; inspect the old backup paths that remain."
                            : "Migration failed before confirming the new backup root. No old backup files were removed. Check the saved-root status below."
                    : result.Failure is not null ? move && result.Failure.Stage == OperationStage.Removing
                    ? result.RemovedPaths.Count > 0
                        ? "Move partially failed during removal. Removed originals were not rolled back. Check reported errors before using backup copies."
                        : "Move failed before removing originals. Check reported errors before using backup copies."
                    : $"{operation} failed. Files already written were not rolled back."
                    : result.WrittenPaths.Count == 0 ? "No items processed." : result.Warnings.Count > 0
                        ? $"{operation} completed with warnings." : $"{operation} completed and verified.";
                summary.Text = $"Written paths (including potentially incomplete files): {result.WrittenPaths.Count}\nVerified files: {result.VerifiedFiles.Count}\n" +
                    string.Join(Environment.NewLine, result.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}")) +
                    (result.Failure is null ? "" : $"\nFailure — {result.Failure.Stage}: {result.Failure.Path}: {result.Failure.Message}") +
                    (result.WrittenPaths.Count == 0 ? "" : "\nWritten paths:\n" + string.Join(Environment.NewLine, result.WrittenPaths)) +
                    (result.VerifiedFiles.Count == 0 ? "" : "\nVerified files:\n" + string.Join(Environment.NewLine, result.VerifiedFiles));
                SetOperationStatus(details, result.Failure is not null
                    ? result.WrittenPaths.Count > 0 || result.RemovedPaths.Count > 0 || result.BackupRootChanged ? "warning" : "error"
                    : result.Warnings.Count > 0 ? "warning" : "success");
                if (migration)
                    summary.Text += $"\nLast known backup root: {viewModel.BackupRoot}\n";
                if (move || migration)
                    summary.Text += $"\nRemoved {(migration ? "old backup" : "original")} paths: {result.RemovedPaths.Count}\n" +
                        (result.RemovedPaths.Count == 0 ? "None." : string.Join(Environment.NewLine, result.RemovedPaths)) +
                        $"\n{(migration ? "Old backup" : "Original")} paths not removed: {result.RemainingPaths.Count}\n" +
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
            SetOperationStatus(details, "error");
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
        var content = new StackPanel { Spacing = 16, Margin = (Thickness)Application.Current!.FindResource("ModalPadding")! };
        var heading = new TextBlock { Text = $"{operation} \u2014 {projectName}" };
        heading.Classes.Add("modal-title");
        content.Children.Add(heading);
        content.Children.Add(new Border { Child = details, Padding = new Thickness(12), CornerRadius = new CornerRadius(8) });
        content.Children.Add(bar);
        content.Children.Add(new ScrollViewer { Content = summary, MaxHeight = 240 });
        content.Children.Add(choices);
        var modal = new Window
        {
            Title = operation, Width = 640, Height = 500, MinWidth = 480, MinHeight = 380,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        return (modal, details, bar, summary, choices, content);
    }

    private static void SetOperationStatus(TextBlock details, string status)
    {
        details.Classes.Add(status);
        if (details.Parent is Border surface) surface.Classes.Add(status);
        details.Text = (status == "success" ? "\u2713 " : status == "warning" ? "\u26A0 Warning: " : "\u2717 Error: ") + details.Text;
    }

    private void Settings_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.Settings);

    private async void NewProject_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCreate) return;
        viewModel.Navigate(MainPanel.Projects);
        viewModel.NewName = "";
        var name = new TextBox { Watermark = "Example: Documents" };
        AutomationProperties.SetName(name, "Project name");
        name.Bind(TextBox.TextProperty, new Binding(nameof(MainWindowViewModel.NewName)) { Mode = BindingMode.TwoWay });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.Classes.Add("error");
        var create = new Button { Content = "Create project" };
        create.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel" };
        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        create.Margin = new Thickness(0, 0, 12, 0);
        choices.Children.Add(create);
        choices.Children.Add(cancel);
        var content = new StackPanel { Spacing = 16, Margin = (Thickness)Application.Current!.FindResource("ModalPadding")! };
        var title = new TextBlock { Text = "New project" };
        title.Classes.Add("modal-title");
        content.Children.Add(new PathIcon { Data = (Geometry)Application.Current!.FindResource("ProjectIcon")!, HorizontalAlignment = HorizontalAlignment.Left, Width = 32, Height = 32 });
        content.Children.Add(title);
        content.Children.Add(new TextBlock { Text = "Project name" });
        content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = "Create a project, then open it to add files and folders." });
        content.Children.Add(error);
        content.Children.Add(choices);
        var modal = new Window
        {
            Title = "New project", Width = Math.Min(520, ClientSize.Width - 48),
            MaxHeight = Math.Max(300, ClientSize.Height - 48), SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = viewModel, Content = new ScrollViewer { Content = content }
        };
        modal.Opened += (_, _) => name.Focus();
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = viewModel.IsBusy;
        modal.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape && !viewModel.IsBusy) { modal.Close(); eventArgs.Handled = true; }
        };
        cancel.Click += (_, _) => { if (!viewModel.IsBusy) modal.Close(); };
        create.Click += async (_, _) =>
        {
            if (!viewModel.CanCreate) return;
            create.IsEnabled = cancel.IsEnabled = name.IsEnabled = false;
            await viewModel.Create();
            if (viewModel.MessageSuccess) modal.Close();
            else
            {
                error.Text = viewModel.StatusMessage;
                create.IsEnabled = viewModel.CanCreate;
                cancel.IsEnabled = name.IsEnabled = true;
                name.Focus();
            }
        };
        await modal.ShowDialog(this);
    }

    private void Projects_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.Projects);

    private void OpenSelected_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel { Selected: { } project } viewModel)
            viewModel.Open(project);
    }

    private void UpdateProjectLayout()
    {
        var width = ClientSize.Width;
        var sidebarWidth = width < 800 ? 180 : 208;
        var padding = width < 800 ? 16 : 24;
        var wide = width >= 1120 && width - sidebarWidth - 2 * padding >= 784;
        Shell.ColumnDefinitions = new ColumnDefinitions($"{sidebarWidth},*");
        MainContent.Margin = new Thickness(padding);
        ProjectLayout.ColumnDefinitions = new ColumnDefinitions(wide ? "440*,320*" : "*");
        ProjectLayout.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto");
        ProjectLayout.ColumnSpacing = wide ? 24 : 0;
        Grid.SetColumn(ProjectSummary, wide ? 1 : 0);
        Grid.SetRow(ProjectSummary, wide ? 0 : 1);
    }

    private void Rename_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Rename();

    private void Open_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: Project project })
            (DataContext as MainWindowViewModel)?.Open(project);
    }

    private void ItemIcon_Loaded(object? sender, RoutedEventArgs args)
    {
        if (sender is PathIcon { DataContext: SelectedItem item } icon)
            icon.Data = (Geometry)Application.Current!.FindResource(item.Type == ItemType.Folder ? "FolderIcon" : "FileIcon")!;
    }

    private void Remove_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SelectedItem item })
            (DataContext as MainWindowViewModel)?.RemoveItem(item);
    }

    private async void Deactivate_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCopy) return;
        var project = viewModel.Selected!;
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var content = new StackPanel { Spacing = 16, Margin = (Thickness)Application.Current!.FindResource("ModalPadding")! };
        content.Children.Add(new TextBlock
        {
            Text = $"Deactivate {project.Name}? It will leave the project list. Registered items, its reserved name and backups will be preserved. You can reactivate it in Settings.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(choices);
        var modal = new Window
        {
            Title = "Deactivate project", Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
        };
        var confirm = new Button { Content = "Deactivate" };
        confirm.Classes.Add("danger");
        var back = new Button { Content = "Back" };
        confirm.Click += (_, _) => modal.Close(true);
        back.Click += (_, _) => modal.Close(false);
        choices.Children.Add(confirm);
        choices.Children.Add(back);
        // Capture the ID before the modal; closing or going back never changes the registry.
        if (await modal.ShowDialog<bool>(this)) viewModel.SetProjectActive(project.Id, false);
    }

    private void Reactivate_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: Project project })
            (DataContext as MainWindowViewModel)?.SetProjectActive(project.Id, true);
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs args) =>
        await SelectItems(ItemType.File);

    private async void AddFolders_Click(object? sender, RoutedEventArgs args) =>
        await SelectItems(ItemType.Folder);

    private async Task SelectItems(ItemType type)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCopy || viewModel.Selected is not { } project) return;
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
