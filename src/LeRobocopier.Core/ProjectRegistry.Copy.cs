using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LeRobocopier.Core;

public sealed partial class ProjectRegistry
{
    public CopyPreparation PrepareCopy(Guid projectId, IProgress<OperationProgress>? progress = null)
        => PrepareCopy(projectId, false, false, progress);

    public CopyPreparation PrepareRestore(Guid projectId, IProgress<OperationProgress>? progress = null)
        => PrepareCopy(projectId, true, false, progress);

    public CopyPreparation PrepareMove(Guid projectId, IProgress<OperationProgress>? progress = null)
        => PrepareCopy(projectId, false, true, progress);

    public CopyPreparation PrepareMigration(string backupRoot, IProgress<OperationProgress>? progress = null)
        => PrepareCopy(Guid.Empty, false, false, progress, backupRoot);

    private CopyPreparation PrepareCopy(Guid projectId, bool restore, bool move, IProgress<OperationProgress>? progress,
        string? migrationRoot = null)
    {
        Guid id;
        lock (gate)
        {
            EnsureIdle();
            if (migrationRoot is null) GetProject(projectId);
            else
            {
                migrationRoot = NormalizePath(migrationRoot);
                if (ContainsPath(State.BackupRoot, migrationRoot) && ContainsPath(migrationRoot, State.BackupRoot))
                    return new CopyPreparation(null, null);
            }
            id = Guid.NewGuid();
            executionId = id;
            copyProjectId = projectId;
            restoreOperation = restore;
            moveOperation = move;
            preparedMigrationRoot = migrationRoot;
            inventoryPath = null;
        }
        try
        {
            var plan = InventoryCopy(id, projectId, restore, move, progress, migrationRoot: migrationRoot);
            preparedOverwrites = plan.Overwrites ?? Array.Empty<string>();
            return new CopyPreparation(plan, null);
        }
        catch (Exception error)
        {
            lock (gate) executionId = null;
            return new CopyPreparation(null, new OperationIssue(OperationStage.Preparing, inventoryPath ?? "", error.Message));
        }
    }

    public void AbandonCopy(CopyPlan plan)
    {
        lock (gate)
        {
            if (executionId != plan.ExecutionId) throw new InvalidOperationException("This operation is no longer active.");
            if (copyRunning) throw new InvalidOperationException("A running operation cannot be abandoned.");
            executionId = null;
        }
    }

    public CopyResult Copy(CopyPlan plan, bool disableConfirmation = false, IProgress<OperationProgress>? progress = null)
        => Copy(plan, false, false, disableConfirmation, progress);

    public CopyResult Restore(CopyPlan plan, bool disableConfirmation = false, IProgress<OperationProgress>? progress = null)
        => Copy(plan, true, false, disableConfirmation, progress);

    public CopyResult Move(CopyPlan plan, bool disableConfirmation = false, IProgress<OperationProgress>? progress = null)
        => Copy(plan, false, true, disableConfirmation, progress);

    public CopyResult Migrate(CopyPlan plan, IProgress<OperationProgress>? progress = null)
        => Copy(plan, false, false, false, progress, true);

    private CopyResult Copy(CopyPlan plan, bool restore, bool move, bool disableConfirmation, IProgress<OperationProgress>? progress,
        bool migration = false)
    {
        lock (gate)
        {
            if (executionId != plan.ExecutionId) throw new InvalidOperationException("This operation is no longer active.");
            if (copyProjectId != plan.ProjectId) throw new InvalidOperationException("The prepared project cannot be changed.");
            if (restoreOperation != restore || plan.IsRestore != restore || moveOperation != move || plan.IsMove != move)
                throw new InvalidOperationException("The prepared operation cannot be changed.");
            if (migration != (preparedMigrationRoot is not null) || plan.MigrationRoot != preparedMigrationRoot)
                throw new InvalidOperationException("The prepared migration cannot be changed.");
            // Claim the prepared operation once, including against callers using the same plan.
            if (copyRunning) throw new InvalidOperationException("This operation is already running.");
            copyRunning = true;
        }
        var written = new List<string>();
        var verified = new List<string>();
        var warnings = new List<OperationIssue>();
        var removed = new List<string>();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var remaining = new HashSet<string>(pathComparer);
        var stage = OperationStage.Preparing;
        var current = "";
        var oldRoot = State.BackupRoot;
        var backupRootChanged = false;
        var migrationRoot = preparedMigrationRoot;
        var stagingRoot = migration ? Path.Combine(migrationRoot!, $".lerobocopier-migration-{plan.ExecutionId:N}") : null;
        var restoreConfirmationRequired = restore && State.Confirmations.Restore;
        inventoryPath = null;
        try
        {
            if (disableConfirmation)
            {
                current = file;
                Save(State with { Confirmations = restore
                    ? State.Confirmations with { Restore = false }
                    : move ? State.Confirmations with { Move = false }
                    : State.Confirmations with { Copy = false } });
            }
            // Confirmation may have taken arbitrarily long; the old inventory authorizes no writes.
            var inventoriedFiles = move || migration ? new Dictionary<string, (long Length, byte[] Hash)>(pathComparer) : null;
            var inventory = InventoryCopy(plan.ExecutionId, plan.ProjectId, restore, move, progress, inventoriedFiles,
                migrationRoot, stagingRoot);
            if (move || migration) remaining.UnionWith(inventory.Entries.Select(entry => entry.Source));
            warnings.AddRange(inventory.Warnings);
            if (migration)
            {
                ValidateMigrationRoot(migrationRoot!);
                inventoryPath = stagingRoot;
                ValidateCopyPath(stagingRoot!, ItemType.Folder, true);
                if (RenameFolderExists(stagingRoot!, false))
                    throw new IOException($"The migration staging folder already exists: {stagingRoot}");
                if (State.Projects.Any(project => pathComparer.Equals(project.Name, Path.GetFileName(stagingRoot))))
                    throw new IOException("The migration staging name conflicts with a registered project.");
                written.Add(stagingRoot!);
                Directory.CreateDirectory(stagingRoot!);
            }
            var confirmedOverwrites = new HashSet<string>(preparedOverwrites, pathComparer);
            var newOverwrite = restoreConfirmationRequired
                ? inventory.Overwrites!.FirstOrDefault(path => !confirmedOverwrites.Contains(path)) : null;
            if (newOverwrite is not null)
            {
                inventoryPath = newOverwrite;
                throw new IOException("The overwrite list changed after preparation. Prepare Restore again to review it.");
            }
            var files = inventory.Entries.Where(entry => entry.Type == ItemType.File).ToArray();
            var expected = new Dictionary<string, (long Length, byte[] Hash)>();
            stage = OperationStage.Copying;
            long completed = 0;
            var total = inventory.Entries.Count;
            foreach (var entry in inventory.Entries)
            {
                current = entry.Source;
                progress?.Report(new OperationProgress(stage, current, completed, total));
                ValidateCopyPath(entry.Source, entry.Type, false);
                ValidateCopyPath(entry.Destination, entry.Type, true);
                if (entry.Type == ItemType.Folder)
                {
                    written.Add(entry.Destination);
                    Directory.CreateDirectory(entry.Destination);
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(entry.Destination, File.GetUnixFileMode(entry.Destination) |
                            UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.UserRead);
                }
                else
                {
                    var parent = Path.GetDirectoryName(entry.Destination)!;
                    if (!Directory.Exists(parent)) written.Add(parent);
                    Directory.CreateDirectory(parent);
                    ValidateCopyPath(entry.Destination, ItemType.File, true);
                    using var source = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.SequentialScan);
                    var sourceLength = source.Length;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var temporary = Path.Combine(parent, $".lerobocopier-{Guid.NewGuid():N}.tmp");
                    written.Add(temporary);
                    try
                    {
                        using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                        {
                            var buffer = new byte[128 * 1024];
                            int count;
                            while ((count = source.Read(buffer)) > 0)
                            {
                                destination.Write(buffer, 0, count);
                                hash.AppendData(buffer, 0, count);
                            }
                            destination.Flush(true);
                            if (destination.Length != sourceLength || source.Length != sourceLength)
                                throw new IOException("The source size changed while copying.");
                        }
                        ValidateCopyPath(entry.Destination, ItemType.File, true);
                        if (restoreConfirmationRequired && File.Exists(entry.Destination) &&
                            !confirmedOverwrites.Contains(entry.Destination))
                            throw new IOException($"A new overwrite appeared during Restore: {entry.Destination}. Prepare Restore again to review it.");
                        if (OperatingSystem.IsWindows() && File.Exists(entry.Destination) && SameWindowsFile(source, entry.Destination))
                            throw new IOException($"The destination is a hard link to the source: {entry.Destination}");
                        if (OperatingSystem.IsWindows() && File.Exists(entry.Destination) &&
                            (File.GetAttributes(entry.Destination) & FileAttributes.ReadOnly) != 0)
                        {
                            // Removing ReadOnly is already a partial effect even if replacement fails.
                            written.Add(entry.Destination);
                            File.SetAttributes(entry.Destination, File.GetAttributes(entry.Destination) & ~FileAttributes.ReadOnly);
                        }
                        // Replacement avoids truncating the source through a destination hard link.
                        File.Move(temporary, entry.Destination,
                            !restoreConfirmationRequired || confirmedOverwrites.Contains(entry.Destination));
                        if (!written.Contains(entry.Destination)) written.Add(entry.Destination);
                        written.Remove(temporary);
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(temporary);
                            written.Remove(temporary);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    expected.Add(entry.Destination, (sourceLength, hash.GetHashAndReset()));
                    if (move || migration)
                    {
                        var inventoried = inventoriedFiles![entry.Source];
                        var copied = expected[entry.Destination];
                        if (copied.Length != inventoried.Length ||
                            !CryptographicOperations.FixedTimeEquals(copied.Hash, inventoried.Hash))
                            throw new IOException("The source changed after inventory. No originals will be removed.");
                    }
                }
                completed++;
                progress?.Report(new OperationProgress(stage, current, completed, total));
            }
            stage = OperationStage.Verifying;
            completed = 0;
            foreach (var entry in files)
            {
                current = entry.Destination;
                ValidateCopyPath(entry.Source, ItemType.File, false);
                ValidateCopyPath(entry.Destination, ItemType.File, false);
                progress?.Report(new OperationProgress(stage, current, completed, files.Length));
                using var destination = File.OpenRead(entry.Destination);
                using var source = File.OpenRead(entry.Source);
                var expectedFile = expected[entry.Destination];
                if (destination.Length != expectedFile.Length || source.Length != expectedFile.Length ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(destination), expectedFile.Hash) ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), expectedFile.Hash))
                    throw new IOException("Size or SHA-256 verification failed. The copied file or source changed.");
                verified.Add(entry.Destination);
                progress?.Report(new OperationProgress(stage, current, ++completed, files.Length));
            }
            stage = OperationStage.Metadata;
            completed = 0;
            // Children first: creating children would otherwise change a folder's modification time.
            foreach (var entry in inventory.Entries.Reverse())
            {
                current = entry.Destination;
                ValidateCopyPath(entry.Source, entry.Type, false);
                ValidateCopyPath(entry.Destination, entry.Type, false);
                if (entry.Type == ItemType.Folder)
                    Directory.SetLastWriteTimeUtc(entry.Destination, Directory.GetLastWriteTimeUtc(entry.Source));
                else
                    File.SetLastWriteTimeUtc(entry.Destination, File.GetLastWriteTimeUtc(entry.Source));
                var common = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
                File.SetAttributes(entry.Destination, (File.GetAttributes(entry.Destination) & ~common) |
                    (File.GetAttributes(entry.Source) & common));
                // Apply modes last; Unix ReadOnly attribute handling can change permission bits.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(entry.Destination, File.GetUnixFileMode(entry.Source));
                progress?.Report(new OperationProgress(stage, current, ++completed, total));
            }
            if (migration)
            {
                stage = OperationStage.Publishing;
                ValidateMigrationRoot(migrationRoot!);
                foreach (var project in State.Projects)
                {
                    var staged = Path.Combine(stagingRoot!, project.Name);
                    if (!inventory.Entries.Any(entry => pathComparer.Equals(entry.Destination, staged))) continue;
                    current = Path.Combine(migrationRoot!, project.Name);
                    progress?.Report(new OperationProgress(stage, current, 0, total));
                    ValidateCopyPath(staged, ItemType.Folder, false);
                    ValidateCopyPath(current, ItemType.Folder, true);
                    // Directory.Move refuses occupied destinations; never merge a preexisting backup.
                    Directory.Move(staged, current);
                    for (var index = 0; index < written.Count; index++)
                        if (ContainsPath(staged, written[index]))
                            written[index] = Path.Combine(current, Path.GetRelativePath(staged, written[index]));
                    for (var index = 0; index < verified.Count; index++)
                        if (ContainsPath(staged, verified[index]))
                            verified[index] = Path.Combine(current, Path.GetRelativePath(staged, verified[index]));
                }
                inventory = inventory with { Entries = Array.AsReadOnly(inventory.Entries.Select(entry => entry with
                {
                    Destination = Path.Combine(migrationRoot!, Path.GetRelativePath(stagingRoot!, entry.Destination))
                }).ToArray()) };
                expected = expected.ToDictionary(pair => Path.Combine(migrationRoot!, Path.GetRelativePath(stagingRoot!, pair.Key)),
                    pair => pair.Value, pathComparer);
                ValidateCopyPath(stagingRoot!, ItemType.Folder, false);
                Directory.Delete(stagingRoot!, false);
                written.Remove(stagingRoot!);

                // Publication and metadata may take time. Verify the complete snapshot again before changing the registry.
                stage = OperationStage.Verifying;
                var authorized = inventory.Entries.ToDictionary(entry => entry.Source, pathComparer);
                var children = inventory.Entries.GroupBy(entry => Path.GetDirectoryName(entry.Source)!, pathComparer)
                    .ToDictionary(group => group.Key,
                        group => new HashSet<string>(group.Select(entry => entry.Source), pathComparer), pathComparer);
                foreach (var entry in inventory.Entries)
                {
                    current = entry.Source;
                    progress?.Report(new OperationProgress(stage, current, 0, total));
                    ValidateMoveEntry(entry, expected, authorized, children, remaining);
                }
                // A previously absent project folder may have appeared while other projects were copied.
                foreach (var project in State.Projects)
                {
                    var source = Path.Combine(oldRoot, project.Name);
                    if (!authorized.ContainsKey(source) && RenameFolderExists(source, false))
                        throw new IOException($"A backup appeared after inventory: {source}. Prepare migration again.");
                    var destination = Path.Combine(migrationRoot!, project.Name);
                    if (!authorized.ContainsKey(source) && RenameFolderExists(destination, false))
                        throw new IOException($"A backup destination became occupied: {destination}.");
                }
                stage = OperationStage.Saving;
                current = file;
                progress?.Report(new OperationProgress(stage, current, 0, total));
                var configurationExisted = RenameFolderExists(file, false);
                try
                {
                    Save(State with { BackupRoot = migrationRoot! });
                    backupRootChanged = true;
                }
                catch (Exception saveError)
                {
                    try
                    {
                        // Replacement can finish before an I/O callback reports failure. Follow the disk decision.
                        try { ReloadSavedConfiguration(); }
                        catch (FileNotFoundException) when (!configurationExisted) { }
                        backupRootChanged = pathComparer.Equals(State.BackupRoot, migrationRoot);
                    }
                    catch (Exception reloadError)
                    {
                        RequiresConfigurationReload = true;
                        throw new IOException("The saved backup root could not be confirmed. All old backups were preserved; restart to reload the configuration. " + reloadError.Message, saveError);
                    }
                    throw new IOException("Saving reported a failure. The saved configuration was reloaded and all old backups were preserved. " + saveError.Message, saveError);
                }
            }
            if (move || migration)
            {
                stage = OperationStage.Removing;
                // Only the fresh, globally verified inventory can authorize removal.
                var authorized = inventory.Entries.ToDictionary(entry => entry.Source, pathComparer);
                var children = inventory.Entries.GroupBy(entry => Path.GetDirectoryName(entry.Source)!, pathComparer)
                    .ToDictionary(group => group.Key,
                        group => new HashSet<string>(group.Select(entry => entry.Source), pathComparer), pathComparer);
                completed = 0;
                // Check every source and backup again before the first destructive action.
                foreach (var entry in inventory.Entries)
                {
                    current = entry.Source;
                    progress?.Report(new OperationProgress(stage, current, 0, total));
                    ValidateMoveEntry(entry, expected, authorized, children, remaining);
                }
                // Reverse inventory order removes children before their parents, never recursively.
                foreach (var entry in inventory.Entries.Reverse())
                {
                    current = entry.Source;
                    progress?.Report(new OperationProgress(stage, current, completed, total));
                    ValidateMoveEntry(entry, expected, authorized, children, remaining);
                    if (entry.Type == ItemType.File)
                        File.Delete(entry.Source);
                    else
                        Directory.Delete(entry.Source, false);
                    removed.Add(entry.Source);
                    remaining.Remove(entry.Source);
                    if (children.TryGetValue(Path.GetDirectoryName(entry.Source)!, out var siblings))
                        siblings.Remove(entry.Source);
                    progress?.Report(new OperationProgress(stage, current, ++completed, total));
                }
            }
            progress?.Report(new OperationProgress(OperationStage.Completed, "", total, total));
            return new CopyResult(written.AsReadOnly(), verified.AsReadOnly(), warnings.AsReadOnly(), null)
            {
                RemovedPaths = removed.AsReadOnly(), RemainingPaths = Array.AsReadOnly(remaining.ToArray()),
                BackupRootChanged = backupRootChanged
            };
        }
        catch (Exception error)
        {
            if (stage == OperationStage.Preparing && inventoryPath is not null) current = inventoryPath;
            return new CopyResult(written.AsReadOnly(), verified.AsReadOnly(), warnings.AsReadOnly(),
                new OperationIssue(stage, current, error.Message))
            {
                RemovedPaths = removed.AsReadOnly(), RemainingPaths = Array.AsReadOnly(remaining.ToArray()),
                BackupRootChanged = backupRootChanged
            };
        }
        finally
        {
            lock (gate)
            {
                copyRunning = false;
                executionId = null;
            }
        }
    }

    private bool copyRunning;
    private Guid copyProjectId;
    private string? inventoryPath;
    private bool restoreOperation;
    private bool moveOperation;
    private string? preparedMigrationRoot;
    private IReadOnlyList<string> preparedOverwrites = Array.Empty<string>();

    private CopyPlan InventoryCopy(Guid id, Guid projectId, bool restore, bool move, IProgress<OperationProgress>? progress,
        Dictionary<string, (long Length, byte[] Hash)>? inventoriedFiles = null,
        string? migrationRoot = null, string? stagingRoot = null)
    {
        var entries = new List<CopyEntry>();
        var warnings = new List<OperationIssue>();
        var overwrites = new List<string>();
        if (migrationRoot is not null) ValidateMigrationRoot(migrationRoot);
        foreach (var project in migrationRoot is null ? [GetProject(projectId)] : State.Projects)
        {
            var destinationRoot = NormalizePath(Path.Combine(stagingRoot ?? migrationRoot ?? State.BackupRoot, project.Name));
            inventoryPath = destinationRoot;
            ValidateCopyPath(destinationRoot, ItemType.Folder, true);
            foreach (var selected in migrationRoot is null ? project.Items :
                [new SelectedItem(ItemType.Folder, Path.Combine(State.BackupRoot, project.Name))])
            {
                var original = NormalizePath(selected.Path);
                var root = Path.GetPathRoot(original)!;
                var rootName = OperatingSystem.IsWindows()
                    ? Path.Combine("UNC", root.Trim(Path.DirectorySeparatorChar)) : "root";
                if (OperatingSystem.IsWindows() && root.Length == 3 && root[1] == ':') rootName = root[..1];
                var backup = migrationRoot is not null ? destinationRoot :
                    NormalizePath(Path.Combine(destinationRoot, rootName, original[root.Length..]));
                if (!ContainsPath(destinationRoot, backup)) throw new IOException($"Invalid mapped destination: {backup}");
                var selectedSource = restore ? backup : original;
                inventoryPath = selectedSource;
                try { ValidateCopyPath(selectedSource, selected.Type, false); }
                catch (FileNotFoundException)
                {
                    warnings.Add(new OperationIssue(OperationStage.Preparing, selectedSource,
                        restore ? "The selected item is missing from the backup." : "The selected item is missing."));
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    warnings.Add(new OperationIssue(OperationStage.Preparing, selectedSource,
                        restore ? "The selected item is missing from the backup." : "The selected item is missing."));
                    continue;
                }
                var pending = new Stack<SelectedItem>();
                pending.Push(new SelectedItem(selected.Type, selectedSource));
                while (pending.TryPop(out var item))
                {
                    var source = NormalizePath(item.Path);
                    inventoryPath = source;
                    ValidateCopyPath(source, item.Type, false);
                    progress?.Report(new OperationProgress(OperationStage.Preparing, source, entries.Count, 0));
                    // Relative descendants are anchored to the registered original, never inferred from a filename.
                    var destination = NormalizePath(Path.Combine(restore ? original : backup,
                        Path.GetRelativePath(selectedSource, source)));
                    if (!ContainsPath(restore ? original : backup, destination))
                        throw new IOException($"Invalid mapped destination: {destination}");
                    inventoryPath = destination;
                    ValidateCopyPath(destination, item.Type, true);
                    entries.Add(new CopyEntry(item.Type, source, destination));
                    if (restore && item.Type == ItemType.File && File.Exists(destination)) overwrites.Add(destination);
                    if (item.Type == ItemType.File)
                    {
                        inventoryPath = source;
                        using var readable = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                            128 * 1024, FileOptions.SequentialScan);
                        if (OperatingSystem.IsWindows() && File.Exists(destination) && SameWindowsFile(readable, destination))
                            throw new IOException($"The destination is a hard link to the source: {destination}");
                        var length = readable.Length;
                        var hash = SHA256.HashData(readable);
                        if ((move || migrationRoot is not null) && readable.Length != length)
                            throw new IOException("The source size changed during inventory.");
                        inventoriedFiles?.Add(source, (length, hash));
                    }
                    if (item.Type == ItemType.Folder)
                    {
                        foreach (var child in Directory.EnumerateFileSystemEntries(source))
                        {
                            inventoryPath = child;
                            var attributes = File.GetAttributes(child);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                throw new IOException($"Links and junctions are unsupported: {child}");
                            pending.Push(new SelectedItem((attributes & FileAttributes.Directory) != 0 ? ItemType.Folder : ItemType.File, child));
                        }
                    }
                }
            }
        }
        return new CopyPlan(id, projectId, entries.AsReadOnly(), warnings.AsReadOnly(), restore, overwrites.AsReadOnly(), move, migrationRoot);
    }

    private void ValidateMigrationRoot(string migrationRoot)
    {
        inventoryPath = migrationRoot;
        if (ContainsPath(State.BackupRoot, migrationRoot) || ContainsPath(migrationRoot, State.BackupRoot))
            throw new ArgumentException("The old and new backup roots must not contain each other.");
        ValidateCopyPath(State.BackupRoot, ItemType.Folder, true);
        ValidateCopyPath(migrationRoot, ItemType.Folder, true);
        foreach (var project in State.Projects)
        {
            foreach (var item in project.Items) ValidateItem(item, Array.Empty<SelectedItem>(), migrationRoot);
            var destination = Path.Combine(migrationRoot, project.Name);
            inventoryPath = destination;
            ValidateCopyPath(destination, ItemType.Folder, true);
            if (RenameFolderExists(destination, false))
                throw new IOException($"The backup destination already exists: {destination}. No folders were merged.");
        }
    }

    private static void ValidateMoveEntry(CopyEntry entry, Dictionary<string, (long Length, byte[] Hash)> expected,
        Dictionary<string, CopyEntry> authorized, Dictionary<string, HashSet<string>> children, HashSet<string> remaining)
    {
        ValidateCopyPath(entry.Source, entry.Type, false);
        ValidateCopyPath(entry.Destination, entry.Type, false);
        if (entry.Type == ItemType.File)
        {
            var expectedFile = expected[entry.Destination];
            using var source = File.OpenRead(entry.Source);
            using var backup = File.OpenRead(entry.Destination);
            if (source.Length != expectedFile.Length || backup.Length != expectedFile.Length ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), expectedFile.Hash) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(backup), expectedFile.Hash))
                throw new IOException("The source or verified backup changed. Removal stopped.");
        }
        // Only selected folders are checked; unrelated siblings of individually selected files are preserved.
        for (var folder = entry.Type == ItemType.Folder ? entry.Source : Path.GetDirectoryName(entry.Source);
            folder is not null && authorized.TryGetValue(folder, out var folderEntry) && folderEntry.Type == ItemType.Folder;
            folder = Path.GetDirectoryName(folder))
        {
            ValidateCopyPath(folder, ItemType.Folder, false);
            var actual = new HashSet<string>(Directory.EnumerateFileSystemEntries(folder).Select(NormalizePath),
                remaining.Comparer);
            var inventoried = children.TryGetValue(folder, out var known) ? known : new HashSet<string>(remaining.Comparer);
            remaining.UnionWith(actual.Except(inventoried, remaining.Comparer));
            if (!actual.SetEquals(inventoried))
                throw new IOException($"The folder contents changed after inventory: {folder}. Removal stopped.");
        }
        // Hashing and enumeration take time; reject newly replaced path components before use.
        ValidateCopyPath(entry.Source, entry.Type, false);
        ValidateCopyPath(entry.Destination, entry.Type, false);
    }

    private static void ValidateCopyPath(string path, ItemType type, bool allowMissing)
    {
        path = NormalizePath(path);
        var ancestors = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            ancestors.Push(parent);
        foreach (var ancestor in ancestors)
            ValidateCopyComponent(ancestor, ItemType.Folder, true);
        ValidateCopyComponent(path, type, allowMissing);
    }

    private static void ValidateCopyComponent(string path, ItemType type, bool allowMissing)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) when (allowMissing) { return; }
        catch (DirectoryNotFoundException) when (allowMissing) { return; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException($"Links, junctions and special devices are unsupported: {path}");
        var folder = (attributes & FileAttributes.Directory) != 0;
        if (folder != (type == ItemType.Folder)) throw new IOException($"The path has an unexpected type: {path}");
        if (OperatingSystem.IsLinux())
        {
            // statx's fixed Linux ABI exposes the file type; FileAttributes alone accepts FIFOs/sockets.
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (Statx(-100, path, 0x100, 1, buffer) != 0)
                    throw new IOException($"Could not inspect the file type: {path} (errno {Marshal.GetLastPInvokeError()}).");
                var mode = (ushort)Marshal.ReadInt16(buffer, 28) & 0xF000;
                if (mode != (type == ItemType.Folder ? 0x4000 : 0x8000))
                    throw new IOException($"Only regular files and folders are supported: {path}");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, IntPtr buffer);

    private static bool SameWindowsFile(FileStream source, string destination)
    {
        using var other = File.OpenHandle(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var first = WindowsFileIdentity(source.SafeFileHandle);
        var second = WindowsFileIdentity(other);
        return first == second;
    }

    private static (int Volume, int High, int Low) WindowsFileIdentity(SafeFileHandle handle)
    {
        // BY_HANDLE_FILE_INFORMATION has fixed DWORD offsets for volume serial and file index.
        var buffer = Marshal.AllocHGlobal(52);
        try
        {
            if (!GetFileInformationByHandle(handle, buffer))
                throw new IOException($"Could not inspect the file identity (error {Marshal.GetLastPInvokeError()}).");
            return (Marshal.ReadInt32(buffer, 28), Marshal.ReadInt32(buffer, 44), Marshal.ReadInt32(buffer, 48));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, IntPtr information);
}
