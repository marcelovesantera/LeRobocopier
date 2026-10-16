using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public partial class MainWindow : Window
{
    private bool isInitializing = true;
    private IDisposable? messageExpiry;
    private int messageVersion;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateEditorLayout();
        EditorPanel.SizeChanged += (_, _) => UpdateEditorLayout();
        Opened += InitializeWindow;
        Closing += (_, args) => args.Cancel = isInitializing || (DataContext as MainWindowViewModel)?.IsBusy == true;
        Closed += (_, _) =>
        {
            Interlocked.Increment(ref messageVersion);
            messageExpiry?.Dispose();
            messageExpiry = null;
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.Dispose();
            }
        };
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
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            Shell.IsVisible = true;
            InitializationScreen.IsVisible = false;
            RestartMessageExpiry(Interlocked.Increment(ref messageVersion));
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref messageVersion);
            messageExpiry?.Dispose();
            messageExpiry = null;
            if (viewModel is not null) viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainWindowViewModel.Message)) return;
        var version = Interlocked.Increment(ref messageVersion);
        if (Dispatcher.UIThread.CheckAccess()) RestartMessageExpiry(version);
        else Dispatcher.UIThread.Post(() => RestartMessageExpiry(version));
    }

    private void RestartMessageExpiry(int version)
    {
        if (version != Volatile.Read(ref messageVersion)) return;
        messageExpiry?.Dispose();
        messageExpiry = null;
        if (DataContext is not MainWindowViewModel { HasMessage: true } viewModel) return;
        messageExpiry = DispatcherTimer.RunOnce(() =>
        {
            if (version == Volatile.Read(ref messageVersion) && ReferenceEquals(DataContext, viewModel))
                viewModel.ClearMessage();
        }, TimeSpan.FromSeconds(6));
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
        var (modal, stage, details, bar, summary, choices, options, report) = CreateOperationDialog(operation, migration ? "All projects" : project!.Name);
        var running = true;
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = running;
        var modalClosed = modal.ShowDialog(this);
        CopyPlan? plan = null;
        var executed = false;
        var progressVersion = 0;
        IProgress<OperationProgress> CreateProgress()
        {
            var version = ++progressVersion;
            return new Progress<OperationProgress>(update =>
            {
                if (version != progressVersion) return;
                SetOperationText(stage, update.Stage switch
                {
                    OperationStage.Preparing => "Preparing files",
                    OperationStage.Copying => restore ? "Restoring files" : "Copying files",
                    OperationStage.Verifying => "Verifying files",
                    OperationStage.Metadata => "Preserving metadata",
                    OperationStage.Publishing => "Publishing backups",
                    OperationStage.Saving => "Saving settings",
                    OperationStage.Removing => migration ? "Removing old backups" : "Removing originals",
                    OperationStage.Completed => "Completed",
                    _ => update.Stage.ToString()
                });
                SetOperationText(details, update.Path);
                bar.IsIndeterminate = update.Total == 0;
                bar.Value = update.Total == 0 ? 0 : 100d * update.Completed / update.Total;
            });
        }
        var progress = CreateProgress();
        try
        {
            var preparation = await Task.Run(() => migration ? viewModel.PrepareMigration(migrationRoot!, progress) : restore
                ? viewModel.PrepareRestore(project!.Id, progress) : move
                    ? viewModel.PrepareMove(project!.Id, progress) : viewModel.PrepareCopy(project!.Id, progress));
            progressVersion++;
            if (preparation.Failure is { } failure)
            {
                SetOperationText(stage, "Preparation failed");
                SetOperationText(details, "Preparation failed. No operation files were changed.");
                summary.ItemsSource = new[] { $"{failure.Stage}: {failure.Path}: {failure.Message}" };
                SetOperationText((TextBlock)report.Header!, "Error details");
                report.IsVisible = true;
                SetOperationStatus(details, "error");
            }
            else if (preparation.Plan is null)
            {
                SetOperationText(stage, "No migration needed");
                SetOperationText(details, "This folder is already the active backup root. No migration is needed.");
            }
            else
            {
                plan = preparation.Plan!;
                var skip = new CheckBox { Content = $"Do not ask again for {operation}" };
                if (migration || (restore ? viewModel.ConfirmRestore : move ? viewModel.ConfirmMove : viewModel.ConfirmCopy))
                {
                    SetOperationText(stage, "Ready for confirmation");
                    SetOperationText(details, migration
                        ? $"Migrate {plan.Entries.Count} file(s) and folder(s) for all active and inactive projects? Complete backups, including extra files, will be copied and verified before saving the new root and removing the old copies. Unrelated files in both roots will be preserved."
                        : $"{operation} {plan.Entries.Count} file(s) and folder(s)? " +
                        (restore ? "Corresponding originals will be replaced. Backup and extra original files will be preserved."
                            : move ? "Originals will be removed only after all copies are verified. Extra backup files will be preserved."
                            : "Originals and extra backup files will be preserved."), true);
                    bar.IsIndeterminate = false;
                    var reviewLines = plan.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}").ToList();
                    if (migration)
                    {
                        reviewLines.Add($"Current backup root: {oldRoot}");
                        reviewLines.Add($"New backup root: {migrationRoot}");
                    }
                    reviewLines.Add("Destinations:");
                    reviewLines.AddRange(plan.Entries.Select(entry => entry.Destination));
                    if (plan.Entries.Count == 0) reviewLines.Add("None.");
                    if (move || migration)
                    {
                        reviewLines.Add(migration ? "Old backup paths to remove after verification and saving:" : "Originals to remove after verification:");
                        reviewLines.AddRange(plan.Entries.Select(entry => entry.Source));
                        if (plan.Entries.Count == 0) reviewLines.Add("None.");
                    }
                    if (restore)
                    {
                        reviewLines.Add("Files to overwrite:");
                        reviewLines.AddRange(plan.Overwrites!);
                        if (plan.Overwrites!.Count == 0) reviewLines.Add("None.");
                    }
                    summary.ItemsSource = reviewLines;
                    SetOperationText((TextBlock)report.Header!, "Review paths and warnings");
                    report.IsVisible = true;
                    report.IsExpanded = false;
                    if (!migration)
                    {
                        options.Children.Add(skip);
                        options.IsVisible = true;
                    }
                    var decision = new TaskCompletionSource<bool>();
                    var proceed = new Button { Content = operation };
                    proceed.Classes.Add("primary");
                    var decline = new Button { Content = "Back" };
                    proceed.Click += (_, _) => decision.TrySetResult(true);
                    decline.Click += (_, _) => decision.TrySetResult(false);
                    choices.Children.Add(proceed);
                    choices.Children.Add(decline);
                    choices.IsVisible = true;
                    ResizeOperationDialog(modal, report);
                    if (!await decision.Task)
                    {
                        viewModel.AbandonCopy(plan);
                        plan = null;
                        running = false;
                        modal.Close();
                        return;
                    }
                    choices.Children.Clear();
                    choices.IsVisible = false;
                    options.Children.Remove(skip);
                    options.IsVisible = false;
                }
                summary.ItemsSource = Array.Empty<string>();
                report.IsVisible = false;
                report.IsExpanded = false;
                ResizeOperationDialog(modal, report);
                var disableConfirmation = skip.IsChecked == true;
                executed = true;
                SetOperationText(stage, "Starting operation");
                SetOperationText(details, $"Starting {operation}...");
                progress = CreateProgress();
                var result = await Task.Run(() => migration ? viewModel.Migrate(plan!, progress) : restore
                    ? viewModel.Restore(plan!, disableConfirmation, progress) : move
                        ? viewModel.Move(plan!, disableConfirmation, progress) : viewModel.Copy(plan!, disableConfirmation, progress));
                progressVersion++;
                SetOperationText(stage, result.Failure is not null ? "Operation failed" : result.Warnings.Count > 0 ? "Completed with warnings" : "Completed");
                SetOperationText(details, migration
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
                        ? $"{operation} completed with warnings." : $"{operation} completed and verified.");
                var resultLines = new List<string>
                {
                    $"Written paths (including potentially incomplete files): {result.WrittenPaths.Count}",
                    $"Verified files: {result.VerifiedFiles.Count}"
                };
                resultLines.AddRange(result.Warnings.Select(warning => $"Warning: {warning.Path}: {warning.Message}"));
                if (result.Failure is { } issue) resultLines.Add($"Failure — {issue.Stage}: {issue.Path}: {issue.Message}");
                if (result.WrittenPaths.Count > 0)
                {
                    resultLines.Add("Written paths:");
                    resultLines.AddRange(result.WrittenPaths);
                }
                if (result.VerifiedFiles.Count > 0)
                {
                    resultLines.Add(move || migration ? "Verified backup files (review reported failures before use):" : "Verified files:");
                    resultLines.AddRange(result.VerifiedFiles);
                }
                SetOperationStatus(details, result.Failure is not null
                    ? result.WrittenPaths.Count > 0 || result.RemovedPaths.Count > 0 || result.BackupRootChanged ? "warning" : "error"
                    : result.Warnings.Count > 0 ? "warning" : "success");
                if (migration)
                    resultLines.Add($"Last known backup root: {viewModel.BackupRoot}");
                if (move || migration)
                {
                    resultLines.Add($"Removed {(migration ? "old backup" : "original")} paths: {result.RemovedPaths.Count}");
                    resultLines.AddRange(result.RemovedPaths);
                    if (result.RemovedPaths.Count == 0) resultLines.Add("None.");
                    resultLines.Add($"{(migration ? "Old backup" : "Original")} paths not removed: {result.RemainingPaths.Count}");
                    resultLines.AddRange(result.RemainingPaths);
                    if (result.RemainingPaths.Count == 0) resultLines.Add("None.");
                }
                summary.ItemsSource = resultLines;
                SetOperationText((TextBlock)report.Header!, $"Details — {result.WrittenPaths.Count} written, {result.VerifiedFiles.Count} verified");
                report.IsVisible = true;
                report.IsExpanded = false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            SetOperationText(stage, "Operation failed");
            SetOperationText(details, $"{operation} could not finish.");
            summary.ItemsSource = new[] { error.Message };
            SetOperationText((TextBlock)report.Header!, "Error details");
            report.IsVisible = true;
            report.IsExpanded = false;
            SetOperationStatus(details, "error");
        }
        finally
        {
            progressVersion++;
            options.Children.Clear();
            options.IsVisible = false;
            if (plan is not null && !executed) viewModel.AbandonCopy(plan);
            running = false;
            bar.IsIndeterminate = false;
            viewModel.IsBusy = false;
            viewModel.RefreshAfterOperation();
        }
        if (modal.IsVisible)
        {
            report.Classes.Add("operation-result");
            var close = new Button { Content = "Close" };
            close.Click += (_, _) => modal.Close();
            choices.Children.Clear();
            choices.Children.Add(close);
            choices.IsVisible = true;
            ResizeOperationDialog(modal, report);
            await modalClosed;
        }
    }

    private static (Window Window, TextBlock Stage, TextBlock Details, ProgressBar Bar, ListBox Summary,
        StackPanel Choices, StackPanel Options, Expander Report) CreateOperationDialog(string operation, string projectName)
    {
        var stage = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold };
        SetOperationText(stage, "Preparing files");
        var details = new TextBlock();
        SetOperationText(details, "Preparing the inventory…");
        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        var summary = new ListBox
        {
            MaxHeight = 180,
            ItemTemplate = new FuncDataTemplate<string>((line, _) =>
            {
                var entry = new TextBlock();
                SetOperationText(entry, line ?? "");
                return entry;
            })
        };
        summary.Classes.Add("operation-lines");
        summary.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var reportTitle = new TextBlock();
        SetOperationText(reportTitle, "Details");
        var report = new Expander
        {
            Header = reportTitle, Content = summary, IsVisible = false,
            Padding = new Thickness(0), Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        var options = new StackPanel { Margin = new Thickness(0, 4), IsVisible = false };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            Margin = (Thickness)Application.Current!.FindResource("ModalPadding")!
        };
        var heading = new TextBlock();
        SetOperationText(heading, $"{operation} \u2014 {projectName}");
        heading.Classes.Add("modal-title");
        heading.Margin = new Thickness(0, 0, 0, 12);
        var statusContent = new StackPanel { Spacing = 4 };
        statusContent.Children.Add(stage);
        statusContent.Children.Add(details);
        var status = new Border { Child = statusContent, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(status, 1);
        Grid.SetRow(bar, 2);
        Grid.SetRow(options, 3);
        Grid.SetRow(report, 4);
        Grid.SetRow(choices, 5);
        content.Children.Add(heading);
        content.Children.Add(status);
        content.Children.Add(bar);
        content.Children.Add(options);
        content.Children.Add(report);
        content.Children.Add(choices);
        var modal = new Window
        {
            Title = operation, Width = 640, MinWidth = 480, MaxHeight = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
        };
        report.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty) ResizeOperationDialog(modal, report);
        };
        return (modal, stage, details, bar, summary, choices, options, report);
    }

    private static void ResizeOperationDialog(Window modal, Expander report)
    {
        var content = (Grid)modal.Content!;
        if (report.Classes.Contains("operation-result"))
        {
            content.RowSpacing = 12;
            foreach (var child in content.Children) child.Margin = new Thickness(0);
            report.ClearValue(Expander.PaddingProperty);
        }
        var expanded = report.IsVisible && report.IsExpanded;
        content.RowDefinitions[4].Height = expanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        modal.Height = expanded ? 560 : double.NaN;
        modal.SizeToContent = expanded ? SizeToContent.Manual : SizeToContent.Height;
    }

    private static void SetOperationText(TextBlock text, string value, bool wrap = false)
    {
        text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        text.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        text.Text = wrap ? value : value.ReplaceLineEndings(" ");
        ToolTip.SetTip(text, value);
    }

    private static void SetOperationStatus(TextBlock details, string status)
    {
        details.Classes.Add(status);
        if (details.Parent?.Parent is Border surface) surface.Classes.Add(status);
        SetOperationText(details, (status == "success" ? "\u2713 " : status == "warning" ? "\u26A0 Warning: " : "\u2717 Error: ") +
            (ToolTip.GetTip(details) as string ?? details.Text ?? ""));
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

    private void UpdateEditorLayout()
    {
        StatusScroll.MaxHeight = ClientSize.Height < 600 ? 48 : 100;
        if (EditorPanel.Bounds.Height <= 0) return;
        var compact = EditorPanel.Bounds.Height < 550;
        if (EditorPanel.Classes.Contains("compact") == compact) return;
        EditorPanel.Classes.Set("compact", compact);
        EditorPanel.RowSpacing = compact ? 8 : 20;
        EditorHeader.ColumnDefinitions = new ColumnDefinitions(compact ? "Auto,*,Auto" : "*");
        EditorHeader.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto,Auto,Auto,Auto" : "Auto,Auto,Auto,Auto,Auto,Auto,Auto");
        EditorHeader.RowSpacing = compact ? 8 : 16;
        EditorHeader.ColumnSpacing = compact ? 12 : 0;
        Grid.SetRow(EditorTitle, compact ? 0 : 1);
        Grid.SetColumn(EditorTitle, compact ? 1 : 0);
        Grid.SetColumnSpan(EditorTitle, compact ? 2 : 1);
        Grid.SetRow(EditorRename, compact ? 1 : 2);
        Grid.SetColumnSpan(EditorRename, compact ? 3 : 1);
        Grid.SetRow(EditorToolbar, compact ? 2 : 3);
        Grid.SetColumnSpan(EditorToolbar, compact ? 2 : 1);
        Grid.SetRow(EditorNote, compact ? 3 : 4);
        Grid.SetColumnSpan(EditorNote, compact ? 3 : 1);
        Grid.SetRow(EditorDeactivate, compact ? 2 : 5);
        Grid.SetColumn(EditorDeactivate, compact ? 2 : 0);
        Grid.SetRow(EditorDivider, compact ? 4 : 6);
        Grid.SetColumnSpan(EditorDivider, compact ? 3 : 1);
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
