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
    private Project? selected;
    private bool isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<Project> Projects => registry?.State.Projects.Where(project => project.Active).ToArray()
        ?? Array.Empty<Project>();
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
    public bool ConfirmMove => registry?.State.Confirmations.Move ?? true;
    public bool ConfirmRestore => registry?.State.Confirmations.Restore ?? true;
    public bool CanCreate => registry is not null && !IsBusy;
    public bool CanCopy => CanCreate && Selected is not null;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            isBusy = value;
            Notify();
            Notify(nameof(CanCreate));
            Notify(nameof(CanCopy));
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
    public Project? Selected
    {
        get => selected;
        set
        {
            selected = value;
            Notify();
            Notify(nameof(Selection));
            Notify(nameof(Items));
            Notify(nameof(ProjectName));
            Notify(nameof(CanCopy));
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

    public CopyPreparation PrepareCopy(Guid projectId, IProgress<OperationProgress> progress) =>
        registry!.PrepareCopy(projectId, progress);

    public CopyResult Copy(CopyPlan plan, bool disableConfirmation, IProgress<OperationProgress> progress) =>
        registry!.Copy(plan, disableConfirmation, progress);

    public void AbandonCopy(CopyPlan plan) => registry!.AbandonCopy(plan);

    public void RefreshConfirmation() => Notify(nameof(ConfirmCopy));

    public void Dispose() => registry?.Dispose();

    private void UpdateProject(Project project)
    {
        var selectedId = Selected?.Id;
        // The ListBox may clear selection when ItemsSource changes; restore it using the stable ID.
        Notify(nameof(Projects));
        Selected = selectedId == project.Id ? project :
            registry!.State.Projects.FirstOrDefault(current => current.Id == selectedId);
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
