using System.ComponentModel;
using System.Runtime.CompilerServices;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public enum MainPanel { Projects, Settings, NewProject, Editor }

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectRegistry? registry;
    private MainPanel panel = MainPanel.Projects;
    private string message = "";
    private string newName = "";
    private string renameName = "";
    private Project? selected;
    private bool isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<Project> Projects => registry?.State.Projects.Where(project => project.Active).ToArray()
        ?? Array.Empty<Project>();
    public IReadOnlyList<Project> InactiveProjects => registry?.State.Projects.Where(project => !project.Active).ToArray()
        ?? Array.Empty<Project>();
    public bool NoInactiveProjects => InactiveProjects.Count == 0;
    public string BackupRoot => registry?.State.BackupRoot ?? "Unavailable";
    public bool ConfirmCopy
    {
        get => registry?.State.Confirmations.Copy ?? true;
        set
        {
            try { registry?.SetCopyConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Message = error.Message;
            }
            Notify();
        }
    }
    public bool ConfirmMove
    {
        get => registry?.State.Confirmations.Move ?? true;
        set
        {
            try { registry?.SetMoveConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Message = error.Message;
            }
            Notify();
        }
    }
    public bool ConfirmRestore
    {
        get => registry?.State.Confirmations.Restore ?? true;
        set
        {
            try { registry?.SetRestoreConfirmation(value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Message = error.Message;
            }
            Notify();
        }
    }
    public bool CanCreate => registry is not null && !registry.RequiresRenameRecovery && !IsBusy;
    public bool CanCopy => CanCreate && Selected is not null;
    public bool CanRestore => CanCopy;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            isBusy = value;
            Notify();
            Notify(nameof(CanCreate));
            Notify(nameof(CanCopy));
            Notify(nameof(CanRestore));
        }
    }
    public bool ShowProjects => panel == MainPanel.Projects;
    public bool ShowSettings => panel == MainPanel.Settings;
    public bool ShowNewProject => panel == MainPanel.NewProject;
    public bool ShowEditor => panel == MainPanel.Editor;
    public IReadOnlyList<SelectedItem> Items => Selected?.Items ?? Array.Empty<SelectedItem>();
    public string ProjectName => Selected?.Name ?? "";
    public bool NoProjects => Projects.Count == 0;
    public string Message { get => message; private set { message = value; Notify(); } }
    public string NewName { get => newName; set { newName = value; Notify(); } }
    public string RenameName { get => renameName; set { renameName = value; Notify(); } }
    public Project? Selected
    {
        get => selected;
        set
        {
            // Keep an active selection on repeated clicks; allow it to clear after deactivation.
            selected = value ?? Projects.FirstOrDefault(project => project.Id == selected?.Id);
            RenameName = selected?.Name ?? "";
            Notify();
            Notify(nameof(Selection));
            Notify(nameof(Items));
            Notify(nameof(ProjectName));
            Notify(nameof(CanCopy));
            Notify(nameof(CanRestore));
        }
    }
    public string Selection => Selected is null ? "Select a project." : $"Selected project: {Selected.Name}";

    public MainWindowViewModel()
    {
        try { registry = new ProjectRegistry(UserDirectories.Configuration(), UserDirectories.BackupRoot()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = error.Message + " Fix the configuration and restart the application.";
        }
    }

    public void Navigate(MainPanel destination)
    {
        if (IsBusy) return;
        panel = destination;
        Notify(nameof(ShowProjects));
        Notify(nameof(ShowSettings));
        Notify(nameof(ShowNewProject));
        Notify(nameof(ShowEditor));
    }

    public void Open(Project project)
    {
        Selected = project;
        Message = "";
        Navigate(MainPanel.Editor);
    }

    public void AddItems(Guid projectId, IReadOnlyList<SelectedItem> items)
    {
        if (registry is null) return;
        try
        {
            var updated = registry.AddItems(projectId, items);
            UpdateProject(updated);
            Message = "Items saved.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = error.Message;
        }
    }

    public void RemoveItem(SelectedItem item)
    {
        if (registry is null || Selected is null) return;
        try
        {
            UpdateProject(registry.RemoveItem(Selected.Id, item));
            Message = "Item removed from the registry. Files were preserved.";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = error.Message;
        }
    }

    public void ReportError(string error) => Message = error;

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
            Message = error.Message;
        }
        Notify(nameof(CanCreate));
        Notify(nameof(CanCopy));
        Notify(nameof(CanRestore));
    }

    public void SetProjectActive(Guid projectId, bool active)
    {
        if (registry is null) return;
        try
        {
            var updated = registry.SetProjectActive(projectId, active);
            UpdateProject(updated);
            Notify(nameof(InactiveProjects));
            Notify(nameof(NoInactiveProjects));
            Notify(nameof(NoProjects));
            Message = active ? "Project reactivated." : "Project deactivated. Registered items and backups were preserved.";
            if (!active && ShowEditor && Selected is null) Navigate(MainPanel.Projects);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = error.Message;
        }
    }

    public CopyPreparation PrepareCopy(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.PrepareCopy(projectId, progress);

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

    public void AbandonCopy(CopyPlan plan) => registry!.AbandonCopy(plan);

    public void RefreshConfirmation()
    {
        Notify(nameof(ConfirmCopy));
        Notify(nameof(ConfirmRestore));
        Notify(nameof(ConfirmMove));
    }

    public void Dispose() => registry?.Dispose();

    private void UpdateProject(Project project)
    {
        var selectedId = Selected?.Id;
        // The ListBox may clear selection when ItemsSource changes; restore it using the stable ID.
        Notify(nameof(Projects));
        Selected = selectedId == project.Id && project.Active ? project :
            registry!.State.Projects.FirstOrDefault(current => current.Id == selectedId && current.Active);
    }

    public void Create()
    {
        if (registry is null) return;
        try
        {
            var project = registry.Create(NewName);
            Notify(nameof(Projects));
            Notify(nameof(NoProjects));
            Selected = project;
            NewName = "";
            Message = "Project saved.";
            Navigate(MainPanel.Projects);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or System.Xml.XmlException or InvalidOperationException)
        {
            Message = error.Message;
        }
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
