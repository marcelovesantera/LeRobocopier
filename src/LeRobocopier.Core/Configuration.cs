namespace LeRobocopier.Core;

public enum ItemType { File, Folder }

public enum ProjectOperation { Copy, Move, Restore }

public sealed record SelectedItem(ItemType Type, string Path);

public sealed record Project(Guid Id, string Name, bool Active, IReadOnlyList<SelectedItem> Items)
{
    public ProjectOperation? LastOperation { get; init; }
    public DateTimeOffset? LastOperationStartedAt { get; init; }
    public string? SourceRoot { get; init; }
    public string? BackupRootOverride { get; init; }
}

public sealed record Confirmations(bool Copy = true, bool Move = true, bool Restore = true);

public sealed record Configuration(int Version, string BackupRoot, Confirmations Confirmations,
    IReadOnlyList<Project> Projects);

public enum OperationStage { Preparing, Copying, Verifying, Metadata, Publishing, Saving, Removing, Completed }

public sealed record OperationProgress(OperationStage Stage, string Path, long Completed, long Total);

public sealed record OperationIssue(OperationStage Stage, string Path, string Message);

public sealed record CopyEntry(ItemType Type, string Source, string Destination);

public sealed record CopyPlan(Guid ExecutionId, Guid ProjectId, IReadOnlyList<CopyEntry> Entries,
    IReadOnlyList<OperationIssue> Warnings, bool IsRestore = false,
    IReadOnlyList<string>? Overwrites = null, bool IsMove = false, string? MigrationRoot = null);

public sealed record CopyPreparation(CopyPlan? Plan, OperationIssue? Failure);

public sealed record CopyResult(IReadOnlyList<string> WrittenPaths, IReadOnlyList<string> VerifiedFiles,
    IReadOnlyList<OperationIssue> Warnings, OperationIssue? Failure)
{
    public bool Success => Failure is null && (WrittenPaths.Count > 0 || BackupRootChanged || ProjectDeleted);
    public bool BackupRootChanged { get; init; }
    public bool ProjectDeleted { get; init; }
    public IReadOnlyList<string> RemovedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemainingPaths { get; init; } = Array.Empty<string>();
}

public static class UserDirectories
{
    public static string Configuration() => Configuration(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    // Explicit context allows validating resolution without changing the environment or real profile.
    public static string Configuration(bool windows, string userFolder, string localAppData, string? xdgConfigHome)
    {
        var configurationRoot = localAppData;
        if (!windows)
        {
            configurationRoot = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome)
                ? xdgConfigHome : Path.Combine(userFolder, ".config");
        }
        return Path.Combine(configurationRoot, "LeRobocopier", "settings.xml");
    }

    public static string BackupRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeRobocopier", "Backups");
}
