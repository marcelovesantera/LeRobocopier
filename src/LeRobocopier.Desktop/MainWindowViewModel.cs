using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public enum MainPanel { Projects, Settings, Editor }

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectRegistry? registry;
    private MainPanel panel = MainPanel.Projects;
    private string message = "";
    private string newName = "";
    private string renameName = "";
    private Project? selected;
    private bool isBusy;
    private bool messageError;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<Project> Projects => registry?.State.Projects.Where(project => project.Active).ToArray()
        ?? Array.Empty<Project>();
    public IReadOnlyList<Project> InactiveProjects => registry?.State.Projects.Where(project => !project.Active).ToArray()
        ?? Array.Empty<Project>();
    public bool NoInactiveProjects => InactiveProjects.Count == 0;
    public bool HasInactiveProjects => !NoInactiveProjects;
    public string BackupRoot => registry?.State.BackupRoot ?? "Unavailable";
    public bool ConfirmCopy
    {
        get => registry?.State.Confirmations.Copy ?? true;
        set
        {
            if (!CanCreate) return;
            try { registry?.SetCopyConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ReportError(error.Message);
            }
            Notify();
        }
    }
    public bool ConfirmMove
    {
        get => registry?.State.Confirmations.Move ?? true;
        set
        {
            if (!CanCreate) return;
            try { registry?.SetMoveConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ReportError(error.Message);
            }
            Notify();
        }
    }
    public bool ConfirmRestore
    {
        get => registry?.State.Confirmations.Restore ?? true;
        set
        {
            if (!CanCreate) return;
            try { registry?.SetRestoreConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ReportError(error.Message);
            }
            Notify();
        }
    }
    public bool CanCreate => registry is not null && !registry.RequiresRenameRecovery && !registry.RequiresConfigurationReload && !IsBusy;
    public bool CanCopy => CanCreate && Selected is not null;
    public bool CanRestore => CanCopy;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            isBusy = value;
            Notify();
            Notify(nameof(CanNavigate));
            Notify(nameof(CanCreate));
            Notify(nameof(CanCopy));
            Notify(nameof(CanRestore));
            Notify(nameof(NoProjects));
        }
    }
    public bool CanNavigate => !IsBusy;
    public bool HasSelection => Selected is not null;
    public bool NoSelection => !HasSelection;
    public bool NoItems => Items.Count == 0;
    public bool HasItems => !NoItems;
    public bool HasProjects => Projects.Count > 0;
    public bool HasMessage => Message.Length > 0;
    public bool MessageError => messageError;
    public bool MessageSuccess => !messageError;
    public string StatusMessage => (MessageError ? "\u2717 Error: " : "\u2713 ") + Message;
    public bool ShowProjects => panel == MainPanel.Projects;
    public bool ShowSettings => panel == MainPanel.Settings;
    public bool ShowEditor => panel == MainPanel.Editor;
    public IReadOnlyList<SelectedItem> Items => Selected?.Items ?? Array.Empty<SelectedItem>();
    public string ProjectName => Selected?.Name ?? "";
    public string ProjectBackupFolder => Selected is null ? "" : Path.Combine(ProjectBackupRoot, Selected.Name);
    public bool NoProjects => CanCreate && Projects.Count == 0;
    public string Message
    {
        get => message;
        private set
        {
            message = value;
            messageError = false;
            Notify();
            Notify(nameof(HasMessage));
            Notify(nameof(MessageError));
            Notify(nameof(MessageSuccess));
            Notify(nameof(StatusMessage));
        }
    }
    public string NewName { get => newName; set { newName = value; Notify(); } }
    public string NewSourceRoot { get; set; } = "";
    public string ProjectSourceFolder => Selected?.SourceRoot ?? "Legacy project (multiple source paths)";
    public string ProjectBackupRoot => Selected?.BackupRootOverride ?? BackupRoot;
    public bool InheritsBackupRoot => Selected?.BackupRootOverride is null;
    public string ProjectDestinationMode => InheritsBackupRoot ? "Global default" : "Custom base";
    public string ProjectLastRun => Selected?.LastOperation is { } operation
        ? $"{operation} \u00b7 {FormatLastRun(Selected.LastOperationStartedAt)}"
        : "Not recorded";
    public string ProjectLastRunDescription => Selected?.LastOperation is { } operation
        ? $"Last attempted operation: {operation}. " +
            (Selected.LastOperationStartedAt is null ? "Start time not recorded." : $"Started {FormatLastRun(Selected.LastOperationStartedAt)} (local time).") +
            " This does not indicate success."
        : "No operation has been recorded.";

    private static string FormatLastRun(DateTimeOffset? startedAt) =>
        startedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "Not recorded";
    public string ProjectItemCounts => $"{Items.Count(item => item.Type == ItemType.File)} file(s), {Items.Count(item => item.Type == ItemType.Folder)} folder(s)";
    public string RenameName { get => renameName; set { renameName = value; Notify(); } }
    public Project? Selected
    {
        get => selected;
        set
        {
            var projects = Projects;
            selected = projects.FirstOrDefault(project => project.Id == value?.Id)
                ?? projects.FirstOrDefault(project => project.Id == selected?.Id)
                ?? projects.FirstOrDefault();
            RenameName = selected?.Name ?? "";
            Notify();
            Notify(nameof(HasSelection));
            Notify(nameof(NoSelection));
            Notify(nameof(NoItems));
            Notify(nameof(HasItems));
            Notify(nameof(Items));
            Notify(nameof(ProjectName));
            Notify(nameof(ProjectBackupFolder));
            Notify(nameof(ProjectSourceFolder));
            Notify(nameof(ProjectBackupRoot));
            Notify(nameof(InheritsBackupRoot));
            Notify(nameof(ProjectDestinationMode));
            Notify(nameof(ProjectLastRun));
            Notify(nameof(ProjectLastRunDescription));
            Notify(nameof(ProjectItemCounts));
            Notify(nameof(CanCopy));
            Notify(nameof(CanRestore));
        }
    }

    public MainWindowViewModel()
    {
        try
        {
            registry = new ProjectRegistry(UserDirectories.Configuration(), UserDirectories.BackupRoot());
            Selected = Projects.FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError(error.Message + " Fix the configuration and restart the application.");
        }
    }

    public void Navigate(MainPanel destination)
    {
        if (IsBusy) return;
        panel = destination;
        Notify(nameof(ShowProjects));
        Notify(nameof(ShowSettings));
        Notify(nameof(ShowEditor));
        // Restore the list highlight from the existing selection when returning to Projects.
        if (destination == MainPanel.Projects) Notify(nameof(Selected));
    }

    public void Open(Project project)
    {
        Selected = project;
        Message = "";
        Navigate(MainPanel.Editor);
    }

    public void AddItems(Guid projectId, IReadOnlyList<SelectedItem> items)
    {
        if (!CanCreate) return;
        try
        {
            var updated = registry!.AddItems(projectId, items);
            UpdateProject(updated);
            Message = "Items saved.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError(error.Message);
        }
    }

    public void RemoveItem(SelectedItem item)
    {
        if (!CanCopy) return;
        try
        {
            UpdateProject(registry!.RemoveItem(Selected!.Id, item));
            Message = "Item removed from the registry. Files were preserved.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError(error.Message);
        }
    }

    public void ReportError(string error)
    {
        Message = error;
        messageError = true;
        Notify(nameof(MessageError));
        Notify(nameof(MessageSuccess));
        Notify(nameof(StatusMessage));
    }

    public void ClearMessage() => Message = "";

    public void Rename()
    {
        if (registry is null || Selected is null || !CanCopy) return;
        var projectId = Selected.Id;
        try
        {
            UpdateProject(registry.Rename(projectId, RenameName));
            Message = "Project renamed. Registered items and backup files were preserved.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Recovery may discover that replacement completed before reporting a disk error.
            var current = registry.State.Projects.First(project => project.Id == projectId);
            if (current != Selected) UpdateProject(current);
            ReportError(error.Message);
        }
        Notify(nameof(CanCreate));
        Notify(nameof(CanCopy));
        Notify(nameof(CanRestore));
    }

    public void SetProjectActive(Guid projectId, bool active)
    {
        if (!CanCreate) return;
        var selectedProjectDeactivated = !active && Selected?.Id == projectId;
        try
        {
            var updated = registry!.SetProjectActive(projectId, active);
            UpdateProject(updated);
            Notify(nameof(InactiveProjects));
            Notify(nameof(NoInactiveProjects));
            Notify(nameof(HasInactiveProjects));
            Notify(nameof(NoProjects));
            Notify(nameof(HasProjects));
            Message = active ? "Project reactivated." : "Project deactivated. Registered items and backups were preserved.";
            if (selectedProjectDeactivated && ShowEditor) Navigate(MainPanel.Projects);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError(error.Message);
        }
    }

    public CopyPreparation PrepareCopy(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.PrepareCopy(projectId, progress);

    public CopyResult DeleteProject(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.Delete(projectId, progress);

    public void RefreshAfterDeletion(CopyResult result)
    {
        var selectedId = Selected?.Id;
        Notify(nameof(Projects));
        Notify(nameof(InactiveProjects));
        Notify(nameof(NoInactiveProjects));
        Notify(nameof(HasInactiveProjects));
        Notify(nameof(NoProjects));
        Notify(nameof(HasProjects));
        Selected = Projects.FirstOrDefault(project => project.Id == selectedId);
        if (result.Failure is null) Message = "Project and its backup permanently deleted. Original files were preserved.";
        else if (result.ProjectDeleted) ReportError("Project and backup deleted. Saving reported an error, but the saved registration confirms deletion. Check the operation report.");
        else ReportError("Deletion did not finish successfully. Check the operation report for removed and remaining backup paths.");
    }

    public CopyResult Copy(CopyPlan plan, bool disableConfirmation, IProgress<OperationProgress> progress) =>
        registry!.Copy(plan, disableConfirmation, progress);

    public CopyPreparation PrepareRestore(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.PrepareRestore(projectId, progress);

    public CopyResult Restore(CopyPlan plan, bool disableConfirmation, IProgress<OperationProgress> progress) =>
        registry!.Restore(plan, disableConfirmation, progress);

    public CopyPreparation PrepareMove(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.PrepareMove(projectId, progress);

    public CopyResult Move(CopyPlan plan, bool disableConfirmation, IProgress<OperationProgress> progress) =>
        registry!.Move(plan, disableConfirmation, progress);

    public CopyPreparation PrepareMigration(string backupRoot, IProgress<OperationProgress> progress) =>
        registry!.PrepareMigration(backupRoot, progress);

    public CopyPreparation PrepareProjectMigration(Guid projectId, string? backupRoot, IProgress<OperationProgress> progress) =>
        registry!.PrepareProjectMigration(projectId, backupRoot, progress);

    public async Task ChangeSourceRoot(Guid projectId, string sourceRoot)
    {
        if (!CanCreate) return;
        try
        {
            IsBusy = true;
            UpdateProject(await Task.Run(() => registry!.ChangeSourceRoot(projectId, sourceRoot)));
            Message = "Source folder saved. Original files were preserved; registered relative paths now use the new folder.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (registry?.State.Projects.FirstOrDefault(project => project.Id == projectId) is { } current)
                UpdateProject(current);
            ReportError(error.Message);
        }
        finally { IsBusy = false; }
    }

    public CopyResult Migrate(CopyPlan plan, IProgress<OperationProgress> progress) =>
        registry!.Migrate(plan, progress);

    public void AbandonCopy(CopyPlan plan) => registry!.AbandonCopy(plan);

    public void RefreshAfterOperation()
    {
        if (Selected is { } project && registry is not null)
            UpdateProject(registry.State.Projects.First(current => current.Id == project.Id));
        Notify(nameof(ConfirmCopy));
        Notify(nameof(ConfirmRestore));
        Notify(nameof(ConfirmMove));
        Notify(nameof(BackupRoot));
        Notify(nameof(ProjectBackupFolder));
    }

    public void Dispose() => registry?.Dispose();

    private void UpdateProject(Project project)
    {
        var selectedId = Selected?.Id;
        // The ListBox may clear selection when ItemsSource changes; restore it using the stable ID.
        Notify(nameof(Projects));
        Notify(nameof(NoProjects));
        Notify(nameof(HasProjects));
        Selected = selectedId == project.Id && project.Active ? project :
            registry!.State.Projects.FirstOrDefault(current => current.Id == selectedId && current.Active);
    }

    public async Task Create()
    {
        if (!CanCreate) return;
        try
        {
            IsBusy = true;
            var name = NewName;
            var sourceRoot = NewSourceRoot;
            var project = await Task.Run(() => registry!.Create(name, sourceRoot));
            IsBusy = false;
            Notify(nameof(Projects));
            Notify(nameof(NoProjects));
            Notify(nameof(HasProjects));
            Selected = project;
            NewName = "";
            Message = "Project saved.";
            Navigate(MainPanel.Projects);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or System.Xml.XmlException or InvalidOperationException)
        {
            ReportError(error.Message);
        }
        finally { IsBusy = false; }
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
