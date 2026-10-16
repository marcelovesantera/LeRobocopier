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
using Avalonia.VisualTree;
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
        ProjectSummary.SizeChanged += (_, _) => ProjectSummaryDetails.Spacing = ProjectSummary.Bounds.Height < 320 ? 4 : 10;
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

    private async void Copy_Click(object? sender, RoutedEventArgs args) => await RunProjectOperation(sender, false);

    private async void Restore_Click(object? sender, RoutedEventArgs args) => await RunProjectOperation(sender, true);

    private async void Move_Click(object? sender, RoutedEventArgs args) => await RunProjectOperation(sender, false, true);

    private async Task RunProjectOperation(object? sender, bool restore, bool move = false)
    {
        if (DataContext is not MainWindowViewModel { CanCreate: true } viewModel ||
            sender is not Button { DataContext: Project clicked }) return;
        var project = viewModel.Projects.FirstOrDefault(current => current.Id == clicked.Id);
        if (project is null || !project.Active) return;
        viewModel.Selected = project;
        await RunOperation(restore, move);
    }

    private async void ChangeBackupRoot_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCreate) return;
        string? backupRoot = null;
        viewModel.IsBusy = true;
        try
        {
            backupRoot = await PickFolder(this, "Choose a new backup root", viewModel.BackupRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            viewModel.ReportError(error.Message);
        }
        finally { viewModel.IsBusy = false; }
        if (backupRoot is not null) await RunOperation(false, migrationRoot: backupRoot);
    }

    private async Task RunOperation(bool restore, bool move = false, string? migrationRoot = null,
        Guid? migrationProjectId = null, bool inheritBackupRoot = false)
    {
        var migration = migrationRoot is not null;
        if (DataContext is not MainWindowViewModel viewModel || !(migration ? viewModel.CanCreate : viewModel.CanCopy)) return;
        var operation = migration ? "Migrate backup root" : restore ? "Restore" : move ? "Move" : "Copy";
        var project = viewModel.Selected;
        var oldRoot = migrationProjectId is null ? viewModel.BackupRoot : viewModel.ProjectBackupRoot;
        viewModel.IsBusy = true;
        var (modal, stage, details, bar, summary, choices, options, report) = CreateOperationDialog(operation,
            migration && migrationProjectId is null ? "Projects using the default destination" : project!.Name);
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
            var preparation = await Task.Run(() => migration ? migrationProjectId is { } targetId
                ? viewModel.PrepareProjectMigration(targetId, inheritBackupRoot ? null : migrationRoot, progress)
                : viewModel.PrepareMigration(migrationRoot!, progress) : restore
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
                var confirmationRequired = migration
                    ? migrationProjectId is null || plan.Entries.Count > 0
                    : restore ? viewModel.ConfirmRestore : move ? viewModel.ConfirmMove : viewModel.ConfirmCopy;
                if (confirmationRequired)
                {
                    SetOperationText(stage, "Ready for confirmation");
                    SetOperationText(details, migration
                        ? $"Migrate {plan.Entries.Count} file(s) and folder(s) for {(migrationProjectId is null ? "projects using the default destination, including inactive projects" : project!.Name)}? Complete backups, including extra files, will be copied and verified before saving the new destination and removing the old copies. Unrelated files will be preserved."
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
                viewModel.RefreshAfterOperation();
                SetOperationText(stage, result.Failure is not null ? "Operation failed" : result.Warnings.Count > 0 ? "Completed with warnings" : "Completed");
                SetOperationText(details, migration
                    ? result.Failure is null ? migrationProjectId is not null && plan.Entries.Count == 0
                        ? "Backup destination saved. No backup files needed migration."
                        : "Migration completed. The new backup root is active."
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
                    resultLines.Add($"Last known backup root: {(migrationProjectId is null ? viewModel.BackupRoot : viewModel.ProjectBackupRoot)}");
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

    private static Grid FolderDialogLayout(string heading, string description, StackPanel body, Control actions)
    {
        var title = new TextBlock { Text = heading };
        title.Classes.Add("modal-title");
        var subtitle = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
        subtitle.Classes.Add("muted");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        header.Children.Add(new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(12),
            Background = (IBrush)Application.Current!.FindResource("SelectionBrush")!,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image
            {
                Source = (IImage)Application.Current!.FindResource("SettingsFolderImage")!, Width = 24, Height = 24
            }
        });
        var titles = new StackPanel { Spacing = 4, Children = { title, subtitle } };
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 20,
            Margin = (Thickness)Application.Current!.FindResource("ModalPadding")!
        };
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actions, 2);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        layout.Children.Add(header);
        layout.Children.Add(scroll);
        layout.Children.Add(actions);
        return layout;
    }

    private static Border FolderDialogSection(string label, params Control[] controls)
    {
        var title = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold };
        var content = new StackPanel { Spacing = 10, Children = { title } };
        foreach (var control in controls) content.Children.Add(control);
        var section = new Border { Padding = new Thickness(16), Child = content };
        section.Classes.Add("folder-section");
        return section;
    }

    private static TextBlock FolderDialogHelp(string text)
    {
        var help = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        help.Classes.Add("muted");
        return help;
    }

    private static Border FolderDialogPath(TextBlock path)
    {
        path.TextWrapping = TextWrapping.NoWrap;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(path, path.Text);
        var field = new Border { Padding = new Thickness(12, 10), Child = path };
        field.Classes.Add("folder-path");
        return field;
    }

    private static Border FolderDialogError(TextBlock message)
    {
        var surface = new Border { Padding = new Thickness(12), IsVisible = false };
        surface.Classes.Add("folder-error");
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        content.Children.Add(new TextBlock { Text = "\u2717", FontWeight = FontWeight.Bold });
        Grid.SetColumn(message, 1);
        content.Children.Add(message);
        surface.Child = content;
        message.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBlock.TextProperty) return;
            surface.IsVisible = !string.IsNullOrWhiteSpace(message.Text);
            if (surface.IsVisible) Dispatcher.UIThread.Post(() => surface.BringIntoView());
        };
        return surface;
    }

    private async void NewProject_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCreate) return;
        viewModel.Navigate(MainPanel.Projects);
        viewModel.NewName = "";
        viewModel.NewSourceRoot = "";
        var name = new TextBox { Watermark = "Example: Documents" };
        AutomationProperties.SetName(name, "Project name");
        name.Bind(TextBox.TextProperty, new Binding(nameof(MainWindowViewModel.NewName)) { Mode = BindingMode.TwoWay });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.Classes.Add("error");
        var create = new Button { Content = "Create project", IsEnabled = false };
        create.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel" };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        choices.Children.Add(cancel);
        choices.Children.Add(create);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(FolderDialogSection("Project name", name));
        var source = new TextBlock { Text = "Choose a source folder", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var browse = new Button { Content = "Choose source folder", VerticalAlignment = VerticalAlignment.Center };
        var sourceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        sourceRow.Children.Add(FolderDialogPath(source));
        Grid.SetColumn(browse, 1);
        sourceRow.Children.Add(browse);
        content.Children.Add(FolderDialogSection("Source folder \u00b7 Required", sourceRow,
            FolderDialogHelp("Only files and folders inside this location can be added after creating the project.")));
        var destination = new TextBlock();
        void UpdateDestination()
        {
            destination.Text = $"{viewModel.BackupRoot}{Path.DirectorySeparatorChar}{viewModel.NewName}";
            ToolTip.SetTip(destination, destination.Text);
            create.IsEnabled = viewModel.CanCreate && !string.IsNullOrWhiteSpace(viewModel.NewName) && viewModel.NewSourceRoot.Length > 0;
        }
        name.TextChanged += (_, _) => UpdateDestination();
        UpdateDestination();
        content.Children.Add(FolderDialogSection("Backup destination", FolderDialogPath(destination),
            FolderDialogHelp("Uses the global backup base with a folder named after this project.")));
        content.Children.Add(FolderDialogError(error));
        var modal = new Window
        {
            Title = "New project", Width = Math.Min(600, ClientSize.Width - 48),
            MaxHeight = Math.Max(300, ClientSize.Height - 48), SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = viewModel, Content = FolderDialogLayout("New project", "Name your project and choose where its original files live.", content, choices)
        };
        modal.Opened += (_, _) => name.Focus();
        browse.Click += async (_, _) =>
        {
            if (!viewModel.CanCreate) return;
            viewModel.IsBusy = true;
            browse.IsEnabled = create.IsEnabled = cancel.IsEnabled = false;
            try
            {
                error.Text = "";
                var folder = await PickFolder(modal, "Choose the project source folder", viewModel.NewSourceRoot);
                if (folder is not null)
                {
                    viewModel.NewSourceRoot = folder;
                    source.Text = folder;
                    ToolTip.SetTip(source, folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.Text = exception.Message;
            }
            finally
            {
                viewModel.IsBusy = false;
                browse.IsEnabled = cancel.IsEnabled = true;
                UpdateDestination();
            }
        };
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = viewModel.IsBusy;
        modal.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape && !viewModel.IsBusy) { modal.Close(); eventArgs.Handled = true; }
        };
        cancel.Click += (_, _) => { if (!viewModel.IsBusy) modal.Close(); };
        create.Click += async (_, _) =>
        {
            if (!viewModel.CanCreate) return;
            create.IsEnabled = cancel.IsEnabled = name.IsEnabled = browse.IsEnabled = false;
            await viewModel.Create();
            if (viewModel.MessageSuccess) modal.Close();
            else
            {
                error.Text = $"Error: {viewModel.Message}";
                UpdateDestination();
                cancel.IsEnabled = name.IsEnabled = browse.IsEnabled = true;
                name.Focus();
            }
        };
        await modal.ShowDialog(this);
    }

    private void Projects_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navigate(MainPanel.Projects);

    private static async Task<IStorageFolder?> ResolvePickerStartFolder(Window owner, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        string? candidate;
        try { candidate = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
        while (candidate is not null)
        {
            try
            {
                var folder = await owner.StorageProvider.TryGetFolderFromPathAsync(candidate);
                if (folder is not null) return folder;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or InvalidOperationException or System.Security.SecurityException)
            {
                // A start location is only a hint; inaccessible folders must not block the picker.
            }
            candidate = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate));
        }
        return null;
    }

    private static async Task<string?> PickFolder(Window owner, string title, string? startPath = null)
    {
        if (!owner.StorageProvider.CanPickFolder) throw new NotSupportedException("The folder picker is unavailable in this environment.");
        using var startFolder = await ResolvePickerStartFolder(owner, startPath);
        var chosen = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title, AllowMultiple = false, SuggestedStartLocation = startFolder
        });
        try
        {
            return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath()
                ?? throw new ArgumentException("Select a folder with a full local path.");
        }
        finally
        {
            foreach (var folder in chosen)
                if (!ReferenceEquals(folder, startFolder)) folder.Dispose();
        }
    }

    private async void ProjectFolders_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel { Selected: { } project } viewModel || !viewModel.CanCopy) return;
        var content = new StackPanel { Spacing = 16 };
        var source = new TextBlock();
        SetOperationText(source, viewModel.ProjectSourceFolder);
        var destination = new TextBlock();
        SetOperationText(destination, viewModel.ProjectBackupFolder);
        var changeSource = new Button { Content = "Change source folder", IsEnabled = project.SourceRoot is not null, VerticalAlignment = VerticalAlignment.Center };
        var changeDestination = new Button { Content = "Choose custom backup base" };
        var inherit = new Button { Content = "Use global default", IsEnabled = !viewModel.InheritsBackupRoot };
        var close = new Button { Content = "Close" };
        var error = new TextBlock();
        error.Classes.Add("error");
        var sourceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        sourceRow.Children.Add(FolderDialogPath(source));
        Grid.SetColumn(changeSource, 1);
        sourceRow.Children.Add(changeSource);
        content.Children.Add(FolderDialogSection("Source folder", sourceRow,
            FolderDialogHelp(project.SourceRoot is null
                ? "This legacy project uses multiple original paths. Its source folder cannot be changed."
                : "Changing this folder keeps each item's relative path. Original files stay in place.")));
        var backupMode = new TextBlock
        {
            Text = viewModel.InheritsBackupRoot ? "Global default" : "Custom base", FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)Application.Current!.FindResource("AccentBrush")!
        };
        var badge = new Border
        {
            Background = (IBrush)Application.Current!.FindResource("SelectionBrush")!,
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left, Child = backupMode
        };
        var backupActions = new WrapPanel { Orientation = Orientation.Horizontal };
        changeDestination.Margin = new Thickness(0, 0, 8, 8);
        inherit.Margin = new Thickness(0, 0, 0, 8);
        backupActions.Children.Add(changeDestination);
        backupActions.Children.Add(inherit);
        content.Children.Add(FolderDialogSection("Backup destination", badge, FolderDialogPath(destination),
            FolderDialogHelp(viewModel.InheritsBackupRoot
                ? "Follows the global backup base. Changing that base also migrates this project's backup."
                : "Uses its own backup base and stays independent of global changes."),
            backupActions, FolderDialogHelp("The project name is added to the base you choose. Existing backups require migration confirmation; without a backup, only the destination is saved.")));
        content.Children.Add(FolderDialogError(error));
        var layout = FolderDialogLayout("Project folders", $"Manage source and backup locations for {project.Name}.", content, close);
        var modal = new Window
        {
            Title = $"Folders — {project.Name}", Width = Math.Min(640, ClientSize.Width - 48), MaxHeight = Math.Max(300, ClientSize.Height - 48),
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        string? chosenSource = null;
        string? chosenDestination = null;
        var useDefault = false;
        var picking = false;
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = picking;
        modal.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape && !picking) { modal.Close(); eventArgs.Handled = true; }
        };
        async Task Choose(bool sourceFolder)
        {
            if (picking) return;
            picking = true;
            layout.IsEnabled = false;
            try
            {
                error.Text = "";
                var startPath = sourceFolder ? project.SourceRoot : project.BackupRootOverride ?? viewModel.BackupRoot;
                var path = await PickFolder(modal, sourceFolder ? "Choose the new source folder" : "Choose the project's backup base", startPath);
                if (sourceFolder) chosenSource = path;
                else chosenDestination = path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.Text = exception.Message;
                viewModel.ReportError(exception.Message);
            }
            finally { picking = false; layout.IsEnabled = true; }
            if (chosenSource is not null || chosenDestination is not null) modal.Close();
        }
        changeSource.Click += async (_, _) => await Choose(true);
        changeDestination.Click += async (_, _) => await Choose(false);
        inherit.Click += (_, _) => { useDefault = true; chosenDestination = viewModel.BackupRoot; modal.Close(); };
        close.Click += (_, _) => modal.Close();
        await modal.ShowDialog(this);
        if (chosenDestination is not null)
            await RunOperation(false, migrationRoot: chosenDestination, migrationProjectId: project.Id, inheritBackupRoot: useDefault);
        else if (chosenSource is not null)
        {
            var confirm = new Button { Content = "Change source folder" };
            confirm.Classes.Add("primary");
            var back = new Button { Content = "Back" };
            var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { back, confirm } };
            var confirmationBody = new StackPanel { Spacing = 16 };
            confirmationBody.Children.Add(FolderDialogSection("New source folder", FolderDialogPath(new TextBlock { Text = chosenSource })));
            confirmationBody.Children.Add(new TextBlock
            {
                Text = "Registered items keep their relative paths. Copy and Move will read the new paths; Restore will write to them. Existing original files and backups will not be moved.",
                TextWrapping = TextWrapping.Wrap
            });
            var confirmation = new Window
            {
                Title = "Change source folder", Width = Math.Min(600, ClientSize.Width - 48),
                MaxHeight = Math.Max(300, ClientSize.Height - 48), SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = FolderDialogLayout("Change source folder", $"Confirm the new source for {project.Name}.", confirmationBody, choices)
            };
            confirm.Click += (_, _) => confirmation.Close(true);
            back.Click += (_, _) => confirmation.Close(false);
            if (await confirmation.ShowDialog<bool>(this)) await viewModel.ChangeSourceRoot(project.Id, chosenSource);
        }
    }

    private void UpdateEditorLayout()
    {
        Shell.Classes.Set("compact", ClientSize.Height < 600);
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

    private void ProjectCard_DoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel { CanCreate: true } viewModel ||
            sender is not Border { DataContext: Project clicked }) return;
        if (args.Source is Visual source && (source is Button || source.FindAncestorOfType<Button>() is not null)) return;
        var project = viewModel.Projects.FirstOrDefault(current => current.Id == clicked.Id);
        if (project is null) return;
        args.Handled = true;
        viewModel.Open(project);
    }

    private void ItemIcon_Loaded(object? sender, RoutedEventArgs args)
    {
        if (sender is Image { DataContext: SelectedItem item } icon)
            icon.Source = (IImage)Application.Current!.FindResource(item.Type == ItemType.Folder ? "SummaryFolderImage" : "FileItemImage")!;
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

    private async void Delete_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.CanCreate ||
            sender is not Button { DataContext: Project clicked }) return;
        var projectId = clicked.Id;
        var project = viewModel.InactiveProjects.FirstOrDefault(current => current.Id == projectId);
        if (project is null || project.Active)
        {
            viewModel.ReportError("Select an inactive project to delete.");
            return;
        }
        var backup = Path.Combine(project.BackupRootOverride ?? viewModel.BackupRoot, project.Name);
        viewModel.IsBusy = true;
        var (modal, stage, details, bar, summary, choices, options, report) = CreateOperationDialog("Delete project", project.Name);
        var running = false;
        var finished = false;
        modal.Closing += (_, eventArgs) => eventArgs.Cancel = running;
        var decision = new TaskCompletionSource<bool>();
        modal.Closed += (_, _) => decision.TrySetResult(false);
        SetOperationText(stage, "Permanent deletion");
        SetOperationText(details, $"Permanently delete inactive project \"{project.Name}\" from Settings and its entire backup folder, including all extra files? This cannot be undone. Original files will be preserved.", true);
        bar.IsIndeterminate = false;
        var backupPath = new TextBlock();
        SetOperationText(backupPath, $"Backup folder: {backup}");
        options.Children.Add(backupPath);
        options.IsVisible = true;
        var confirm = new Button { Content = "Delete permanently" };
        confirm.Classes.Add("danger");
        var back = new Button { Content = "Back" };
        confirm.Click += (_, _) => { running = true; decision.TrySetResult(true); };
        back.Click += (_, _) => decision.TrySetResult(false);
        choices.Children.Add(confirm);
        choices.Children.Add(back);
        choices.IsVisible = true;
        var modalClosed = modal.ShowDialog(this);
        try
        {
            if (!await decision.Task)
            {
                modal.Close();
                return;
            }
            // The modal may have been open for a while; resolve the captured identity again.
            project = viewModel.InactiveProjects.FirstOrDefault(current => current.Id == projectId);
            if (project is null || project.Active || Path.Combine(project.BackupRootOverride ?? viewModel.BackupRoot, project.Name) != backup)
                throw new InvalidOperationException("The inactive project changed. Start deletion again.");
            running = true;
            choices.Children.Clear();
            choices.IsVisible = false;
            options.Children.Clear();
            options.IsVisible = false;
            SetOperationText(stage, "Preparing deletion");
            SetOperationText(details, backup);
            var progress = new Progress<OperationProgress>(update =>
            {
                if (finished) return;
                SetOperationText(stage, update.Stage == OperationStage.Removing ? "Deleting backup files" :
                    update.Stage == OperationStage.Saving ? "Saving settings" : "Preparing deletion");
                SetOperationText(details, update.Path);
                bar.IsIndeterminate = update.Total == 0;
                bar.Value = update.Total == 0 ? 0 : 100d * update.Completed / update.Total;
            });
            var result = await Task.Run(() => viewModel.DeleteProject(projectId, progress));
            finished = true;
            if (result.ProjectDeleted) bar.Value = 100;
            SetOperationText(stage, result.Failure is null ? "Project deleted" : result.ProjectDeleted ? "Project deleted with warnings" : "Deletion failed");
            SetOperationText(details, result.Failure is null
                ? "The project and its backup were permanently deleted. Original files were preserved."
                : result.ProjectDeleted
                    ? "The project and its backup were permanently deleted. Saving reported an error, but the saved registration confirms deletion. Original files were preserved."
                    : "Deletion did not finish successfully. Removed backup files cannot be recovered by the application. Inspect the details before retrying.");
            var lines = new List<string>
            {
                $"Project: {project.Name}", $"Backup folder: {backup}",
                $"Registry status: {(result.ProjectDeleted ? "project removed" : "project retained unless saved state is unconfirmed; restart if changes are blocked")}."
            };
            if (result.Failure is { } failure) lines.Add($"Failure — {failure.Stage}: {failure.Path}: {failure.Message}");
            lines.Add($"Removed backup paths: {result.RemovedPaths.Count}");
            lines.AddRange(result.RemovedPaths);
            lines.Add($"Backup paths not removed: {result.RemainingPaths.Count}");
            lines.AddRange(result.RemainingPaths);
            summary.ItemsSource = lines;
            SetOperationText((TextBlock)report.Header!, $"Details — {result.RemovedPaths.Count} removed");
            SetOperationStatus(details, result.Failure is null ? "success" : result.ProjectDeleted || result.RemovedPaths.Count > 0 ? "warning" : "error");
            viewModel.RefreshAfterDeletion(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            finished = true;
            SetOperationText(stage, "Deletion failed");
            SetOperationText(details, "The project could not be deleted. Inspect the details.");
            summary.ItemsSource = new[] { error.Message };
            SetOperationStatus(details, "error");
            viewModel.ReportError(error.Message);
        }
        finally
        {
            finished = true;
            running = false;
            bar.IsIndeterminate = false;
            viewModel.IsBusy = false;
        }
        if (modal.IsVisible)
        {
            report.IsVisible = true;
            report.IsExpanded = false;
            report.Classes.Add("operation-result");
            choices.Children.Clear();
            var close = new Button { Content = "Close" };
            close.Click += (_, _) => modal.Close();
            choices.Children.Add(close);
            choices.IsVisible = true;
            ResizeOperationDialog(modal, report);
            await modalClosed;
        }
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
            viewModel.IsBusy = true;
            var startPath = project.SourceRoot;
            if (startPath is null && project.Items.FirstOrDefault() is { } legacyItem)
                startPath = legacyItem.Type == ItemType.Folder ? legacyItem.Path : Path.GetDirectoryName(legacyItem.Path);
            using var startFolder = await ResolvePickerStartFolder(this, startPath);
            IReadOnlyList<IStorageItem> chosen = type == ItemType.File
                ? await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Add files", AllowMultiple = true, SuggestedStartLocation = startFolder
                })
                : await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Add folders", AllowMultiple = true, SuggestedStartLocation = startFolder
                });
            try
            {
                if (chosen.Count == 0) return;
                var items = chosen.Select(item => new SelectedItem(type,
                    item.TryGetLocalPath() ?? throw new ArgumentException("Select items with full local paths."))).ToArray();
                // Release the picker guard before AddItems checks CanCreate; no await can change the captured target.
                viewModel.IsBusy = false;
                viewModel.AddItems(project.Id, items);
            }
            finally
            {
                foreach (var item in chosen)
                    if (!ReferenceEquals(item, startFolder)) item.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            viewModel.ReportError(error.Message);
        }
        finally { viewModel.IsBusy = false; }
    }
}
