using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LeRobocopier.Core;

// Changes become visible only after replacing the XML file on disk.
public sealed partial class ProjectRegistry : IDisposable
{
    private readonly string file;
    private readonly Action<string, string> replaceFile;
    private readonly object gate = new();
    private readonly FileStream instanceLock;
    private Guid? executionId;
    private bool disposed;
    public bool RequiresRenameRecovery { get; private set; }
    public bool RequiresConfigurationReload { get; private set; }

    public Configuration State { get; private set; }

    public ProjectRegistry(string file, string defaultRoot,
        Action<string, string>? replaceFile = null)
    {
        this.file = Path.GetFullPath(file);
        var initial = Initial(defaultRoot);
        this.replaceFile = replaceFile ?? ((temporary, destination) => File.Move(temporary, destination, true));
        Directory.CreateDirectory(Path.GetDirectoryName(this.file)!);
        try
        {
            // Keep the lock file in place: deleting it could allow two leases on different inodes.
            instanceLock = new FileStream(this.file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Another application instance is using this configuration, or its lock is inaccessible.", error);
        }
        // File.Exists also returns false on access errors; opening the file protects unreadable state.
        try
        {
            using var stream = File.OpenRead(this.file);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            State = Read(XDocument.Load(reader));
        }
        catch (FileNotFoundException)
        {
            State = initial;
        }
        catch (DirectoryNotFoundException)
        {
            State = initial;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or XmlException
            or FormatException or InvalidOperationException or ArgumentException)
        {
            instanceLock.Dispose();
            throw new InvalidDataException($"Could not read {this.file}. The existing file was preserved.", error);
        }
        try { RecoverRename(); }
        catch (Exception error)
        {
            instanceLock.Dispose();
            throw new InvalidDataException("Could not recover the pending project rename. Configuration and backup folders were preserved. " + error.Message, error);
        }
    }

    public Project Create(string name, string sourceRoot)
    {
        lock (gate)
        {
            EnsureIdle();
            ValidateName(name);
            if (State.Projects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active or inactive project already has this name.", nameof(name));

            sourceRoot = NormalizePath(sourceRoot);
            ValidateCopyPath(sourceRoot, ItemType.Folder, false);
            var project = new Project(Guid.NewGuid(), name, true, Array.Empty<SelectedItem>()) { SourceRoot = sourceRoot };
            ValidateProjectPaths(project, State);
            var projects = Array.AsReadOnly(State.Projects.Append(project).ToArray());
            Save(State with { Projects = projects });
            return project;
        }
    }

    private static Configuration Initial(string defaultRoot)
    {
        if (!Path.IsPathFullyQualified(defaultRoot))
            throw new ArgumentException("The backup root must have an absolute path.", nameof(defaultRoot));
        return new Configuration(1, NormalizePath(defaultRoot), new Confirmations(), Array.Empty<Project>());
    }

    public Project AddItems(Guid projectId, IReadOnlyList<SelectedItem> selection)
    {
        lock (gate)
        {
            EnsureIdle();
            var project = GetProject(projectId);
            var items = project.Items.ToList();
            foreach (var selected in selection)
            {
                var item = selected with { Path = NormalizePath(selected.Path) };
                if (project.SourceRoot is not null && !ContainsPath(project.SourceRoot, item.Path))
                    throw new ArgumentException("Select files and folders inside the project's source folder.");
                ValidateItem(item, items, project.BackupRootOverride ?? State.BackupRoot);
                foreach (var registered in State.Projects)
                    ValidateItem(item, Array.Empty<SelectedItem>(), Path.Combine(registered.BackupRootOverride ?? State.BackupRoot, registered.Name));
                var exists = item.Type == ItemType.Folder ? Directory.Exists(item.Path) : File.Exists(item.Path);
                if (!exists) throw new ArgumentException($"The selected {item.Type.ToString().ToLowerInvariant()} does not exist or is inaccessible: {item.Path}");
                items.Add(item);
            }
            if (selection.Count == 0) return project;
            return UpdateItems(project, items);
        }
    }

    public Project RemoveItem(Guid projectId, SelectedItem item)
    {
        lock (gate)
        {
            EnsureIdle();
            var project = GetProject(projectId);
            if (!project.Items.Contains(item)) throw new ArgumentException("The item does not belong to this project.", nameof(item));
            return UpdateItems(project, project.Items.Where(registered => registered != item).ToList());
        }
    }

    public Project ChangeSourceRoot(Guid projectId, string sourceRoot)
    {
        lock (gate)
        {
            EnsureIdle();
            var project = GetProject(projectId);
            if (project.SourceRoot is null)
                throw new InvalidOperationException("This legacy project uses absolute backup paths. Create a new project to choose a source folder.");
            sourceRoot = NormalizePath(sourceRoot);
            ValidateCopyPath(sourceRoot, ItemType.Folder, false);
            var updated = project with
            {
                SourceRoot = sourceRoot,
                Items = Array.AsReadOnly(project.Items.Select(item => item with
                {
                    Path = NormalizePath(Path.Combine(sourceRoot, Path.GetRelativePath(project.SourceRoot, item.Path)))
                }).ToArray())
            };
            ValidateProjectPaths(updated, State);
            var items = new List<SelectedItem>();
            foreach (var item in updated.Items)
            {
                ValidateItem(item, items, updated.BackupRootOverride ?? State.BackupRoot);
                ValidateCopyPath(item.Path, item.Type, true);
                items.Add(item);
            }
            try
            {
                Save(State with { Projects = Array.AsReadOnly(State.Projects.Select(current => current.Id == projectId ? updated : current).ToArray()) });
            }
            catch
            {
                try { ReloadSavedConfiguration(); }
                catch { RequiresConfigurationReload = true; }
                throw;
            }
            return updated;
        }
    }

    private static void ValidateProjectPaths(Project project, Configuration configuration)
    {
        var backupRoot = project.BackupRootOverride ?? configuration.BackupRoot;
        if (project.SourceRoot is { } sourceRoot)
        {
            ValidateCopyPath(sourceRoot, ItemType.Folder, true);
            ValidateItem(new SelectedItem(ItemType.Folder, sourceRoot), Array.Empty<SelectedItem>(), backupRoot);
        }
        foreach (var item in project.Items) ValidateItem(item, Array.Empty<SelectedItem>(), backupRoot);
        var backup = NormalizePath(Path.Combine(backupRoot, project.Name));
        foreach (var registered in configuration.Projects.Where(current => current.Id != project.Id).Append(project))
        {
            if (registered.SourceRoot is { } source && (ContainsPath(source, backup) || ContainsPath(backup, source)))
                throw new ArgumentException($"The backup overlaps a project source folder: {source}");
            foreach (var item in registered.Items)
                ValidateItem(item, Array.Empty<SelectedItem>(), backup);
            if (registered.Id == project.Id) continue;
            var otherBackup = NormalizePath(Path.Combine(registered.BackupRootOverride ?? configuration.BackupRoot, registered.Name));
            if (ContainsPath(backup, otherBackup) || ContainsPath(otherBackup, backup))
                throw new ArgumentException($"Project backup folders overlap: {backup}; {otherBackup}");
            if (project.SourceRoot is { } projectSource && (ContainsPath(projectSource, otherBackup) || ContainsPath(otherBackup, projectSource)))
                throw new ArgumentException($"The source folder overlaps a project backup: {otherBackup}");
            foreach (var item in project.Items) ValidateItem(item, Array.Empty<SelectedItem>(), otherBackup);
        }
    }

    public Project SetProjectActive(Guid projectId, bool active)
    {
        lock (gate)
        {
            EnsureIdle();
            var project = State.Projects.FirstOrDefault(current => current.Id == projectId)
                ?? throw new ArgumentException("The project does not exist.", nameof(projectId));
            if (project.Active == active) return project;
            var updated = project with { Active = active };
            var projects = Array.AsReadOnly(State.Projects.Select(current => current.Id == projectId ? updated : current).ToArray());
            Save(State with { Projects = projects });
            return updated;
        }
    }

    public CopyResult Delete(Guid projectId, IProgress<OperationProgress>? progress = null)
    {
        Project project;
        lock (gate)
        {
            EnsureIdle();
            project = State.Projects.FirstOrDefault(current => current.Id == projectId)
                ?? throw new ArgumentException("The project does not exist.", nameof(projectId));
            if (project.Active) throw new InvalidOperationException("Only inactive projects can be deleted.");
            executionId = Guid.NewGuid();
            copyRunning = true;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = new List<CopyEntry>();
        var expected = new Dictionary<string, (long Length, byte[] Hash)>(comparer);
        var remaining = new HashSet<string>(comparer);
        var removed = new List<string>();
        var stage = OperationStage.Preparing;
        var backupRoot = project.BackupRootOverride ?? State.BackupRoot;
        var current = backupRoot;
        var deleted = false;
        try
        {
            ValidateName(project.Name);
            ValidateProjectPaths(project, State);
            var backup = NormalizePath(Path.Combine(backupRoot, project.Name));
            current = backup;
            if (!ContainsPath(backupRoot, backup) || comparer.Equals(backupRoot, backup) ||
                ContainsPath(backup, file) || ContainsPath(backup, file + ".lock"))
                throw new IOException("The project backup folder is unsafe for deletion.");
            foreach (var item in State.Projects.SelectMany(registered => registered.Items))
                if (ContainsPath(backup, item.Path) || ContainsPath(item.Path, backup))
                    throw new IOException($"The backup overlaps a registered original: {item.Path}");
            current = file;
            ValidateCopyPath(file, ItemType.File, false);
            current = file + ".lock";
            ValidateCopyPath(current, ItemType.File, false);
            current = backup;
            remaining.Add(backup);
            ValidateCopyPath(backup, ItemType.Folder, true);
            var pending = new Stack<SelectedItem>();
            if (RenameFolderExists(backup, false)) pending.Push(new SelectedItem(ItemType.Folder, backup));
            else remaining.Remove(backup);
            while (pending.TryPop(out var item))
            {
                current = NormalizePath(item.Path);
                remaining.Add(current);
                if (!ContainsPath(backup, current)) throw new IOException($"Path outside the project backup: {current}");
                ValidateCopyPath(current, item.Type, false);
                progress?.Report(new OperationProgress(stage, current, entries.Count, 0));
                entries.Add(new CopyEntry(item.Type, current, current));
                if (item.Type == ItemType.File)
                {
                    using var stream = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.SequentialScan);
                    var length = stream.Length;
                    var hash = System.Security.Cryptography.SHA256.HashData(stream);
                    if (stream.Length != length) throw new IOException("The backup changed during inventory.");
                    expected.Add(current, (length, hash));
                }
                else
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    {
                        current = child;
                        remaining.Add(NormalizePath(child));
                        var attributes = File.GetAttributes(child);
                        pending.Push(new SelectedItem((attributes & FileAttributes.Directory) != 0 ? ItemType.Folder : ItemType.File, child));
                    }
            }
            var authorized = entries.ToDictionary(entry => entry.Source, comparer);
            var children = entries.Where(entry => !comparer.Equals(entry.Source, backup))
                .GroupBy(entry => Path.GetDirectoryName(entry.Source)!, comparer)
                .ToDictionary(group => group.Key, group => new HashSet<string>(group.Select(entry => entry.Source), comparer), comparer);
            foreach (var entry in entries)
            {
                current = entry.Source;
                ValidateMoveEntry(entry, expected, authorized, children, remaining);
            }
            stage = OperationStage.Saving;
            current = file;
            progress?.Report(new OperationProgress(stage, current, 0, 0));
            ValidateSavedConfiguration();
            // Prove persistence is available while retaining the inactive entry for any partial deletion.
            Save(State);
            stage = OperationStage.Removing;
            // Resolve the identity again immediately before destructive work, rather than trusting the UI snapshot.
            var registeredProject = State.Projects.FirstOrDefault(registered => registered.Id == projectId);
            if (registeredProject is null || registeredProject.Active || registeredProject.Name != project.Name)
                throw new InvalidOperationException("The inactive project changed. Prepare deletion again.");
            foreach (var entry in entries.OrderByDescending(entry => entry.Source.Length))
            {
                current = entry.Source;
                progress?.Report(new OperationProgress(stage, current, removed.Count, entries.Count));
                ValidateMoveEntry(entry, expected, authorized, children, remaining);
                if (entry.Type == ItemType.File) File.Delete(current);
                else Directory.Delete(current, false);
                removed.Add(current);
                remaining.Remove(current);
                if (children.TryGetValue(Path.GetDirectoryName(current)!, out var siblings)) siblings.Remove(current);
            }
            current = backup;
            ValidateCopyPath(backup, ItemType.Folder, true);
            if (RenameFolderExists(backup, false))
            {
                remaining.Add(backup);
                throw new IOException("The backup folder appeared again. The project registry entry was preserved.");
            }
            stage = OperationStage.Saving;
            current = file;
            progress?.Report(new OperationProgress(stage, current, 0, 0));
            ValidateSavedConfiguration();
            try
            {
                Save(State with { Projects = Array.AsReadOnly(State.Projects.Where(registered => registered.Id != projectId).ToArray()) });
                deleted = true;
            }
            catch
            {
                try
                {
                    ReloadSavedConfiguration();
                    deleted = State.Projects.All(registered => registered.Id != projectId);
                }
                catch { RequiresConfigurationReload = true; }
                throw;
            }
            return new CopyResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<OperationIssue>(), null)
            {
                ProjectDeleted = deleted, RemovedPaths = removed.AsReadOnly(), RemainingPaths = Array.AsReadOnly(remaining.ToArray())
            };
        }
        catch (Exception error)
        {
            if (stage == OperationStage.Removing)
                foreach (var folder in entries.Where(entry => entry.Type == ItemType.Folder && remaining.Contains(entry.Source)))
                {
                    try
                    {
                        ValidateCopyPath(folder.Source, ItemType.Folder, false);
                        remaining.UnionWith(Directory.EnumerateFileSystemEntries(folder.Source).Select(NormalizePath));
                    }
                    catch (Exception inspectionError) when (inspectionError is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            return new CopyResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<OperationIssue>(),
                new OperationIssue(stage, current, error.Message))
            {
                ProjectDeleted = deleted, RemovedPaths = removed.AsReadOnly(), RemainingPaths = Array.AsReadOnly(remaining.ToArray())
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

    private void ValidateSavedConfiguration()
    {
        try
        {
            ValidateCopyPath(file, ItemType.File, false);
            ValidateCopyPath(file + ".lock", ItemType.File, false);
            using var stream = File.OpenRead(file);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var saved = Read(XDocument.Load(reader));
            if (saved.Version != State.Version || saved.BackupRoot != State.BackupRoot || saved.Confirmations != State.Confirmations ||
                saved.Projects.Count != State.Projects.Count || saved.Projects.Where((project, index) =>
                    project.Id != State.Projects[index].Id || project.Name != State.Projects[index].Name ||
                    project.Active != State.Projects[index].Active || project.LastOperation != State.Projects[index].LastOperation ||
                    project.LastOperationStartedAt != State.Projects[index].LastOperationStartedAt ||
                    project.SourceRoot != State.Projects[index].SourceRoot || project.BackupRootOverride != State.Projects[index].BackupRootOverride ||
                    !project.Items.SequenceEqual(State.Projects[index].Items)).Any())
                throw new InvalidDataException("The saved configuration changed outside the application.");
        }
        catch (Exception error)
        {
            RequiresConfigurationReload = true;
            throw new IOException("The saved configuration could not be confirmed. Restart the application before making changes. " + error.Message, error);
        }
    }

    public Project Rename(Guid projectId, string name)
    {
        lock (gate)
        {
            EnsureIdle();
            ValidateName(name);
            var project = State.Projects.FirstOrDefault(current => current.Id == projectId)
                ?? throw new ArgumentException("The project does not exist.", nameof(projectId));
            if (State.Projects.Any(current => current.Id != projectId &&
                string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active or inactive project already has this name.", nameof(name));
            if (project.Name == name) return project;

            var backupRoot = project.BackupRootOverride ?? State.BackupRoot;
            ValidateProjectPaths(project with { Name = name }, State);
            var source = Path.Combine(backupRoot, project.Name);
            var destination = Path.Combine(backupRoot, name);
            ValidateCopyPath(source, ItemType.Folder, true);
            ValidateCopyPath(destination, ItemType.Folder, true);
            var caseOnly = OperatingSystem.IsWindows() && string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase);
            if (RenameFolderExists(destination, caseOnly))
                throw new IOException($"The backup destination already exists: {destination}. No folders were merged.");
            var updated = project with { Name = name };
            var projects = Array.AsReadOnly(State.Projects.Select(current => current.Id == projectId ? updated : current).ToArray());
            var proposal = State with { Projects = projects };
            if (!RenameFolderExists(source, caseOnly))
            {
                // A case-sensitive Windows folder may have unexpected physical casing; do not abandon it.
                if (caseOnly && Directory.Exists(source))
                    throw new IOException($"The backup folder casing does not match the registry: {source}.");
                try
                {
                    Save(proposal);
                    return updated;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    try { ReloadSavedConfiguration(); }
                    catch (Exception recoveryError)
                    {
                        RequiresRenameRecovery = true;
                        throw new IOException("Rename failed and the saved configuration could not be reloaded. Changes are blocked. Restart to reload it. " + recoveryError.Message, error);
                    }
                    throw new IOException("Rename did not finish successfully. The saved configuration was reloaded. " + error.Message, error);
                }
            }

            var temporaryName = $".rename-{projectId:N}-{Guid.NewGuid():N}";
            var temporaryFolder = Path.Combine(backupRoot, temporaryName);
            ValidateCopyPath(temporaryFolder, ItemType.Folder, true);
            if (RenameFolderExists(temporaryFolder, false) || State.Projects.Any(current =>
                string.Equals(current.Name, temporaryName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The temporary rename destination already exists.");
            var pending = new XDocument(new XElement("Rename", new XAttribute("id", projectId),
                new XAttribute("root", backupRoot), new XAttribute("old", project.Name),
                new XAttribute("new", name), new XAttribute("temporary", temporaryName)));
            var journal = file + ".rename.xml";
            var journalTemporary = journal + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(journalTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    pending.Save(stream);
                    stream.Flush(true);
                }
                File.Move(journalTemporary, journal);
            }
            finally
            {
                try { File.Delete(journalTemporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            RequiresRenameRecovery = true;
            try
            {
                // The intermediate sibling also makes case-only renames explicit on Windows.
                ValidateCopyPath(source, ItemType.Folder, false);
                Directory.Move(source, temporaryFolder);
                ValidateCopyPath(destination, ItemType.Folder, true);
                Directory.Move(temporaryFolder, destination);
                Save(proposal);
                RecoverRename();
                return updated;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                try
                {
                    // Inspect the persisted decision, including a replacement that succeeded before reporting an error.
                    ReloadSavedConfiguration();
                    RecoverRename();
                }
                catch (Exception recoveryError)
                {
                    throw new IOException($"Rename failed and recovery is pending. Changes are blocked. Backup candidates: {source}; {temporaryFolder}; {destination}. Restart to retry recovery. {recoveryError.Message}", error);
                }
                throw new IOException("Rename did not finish successfully. The registry locates the preserved backup. " + error.Message, error);
            }
        }
    }

    private void ReloadSavedConfiguration()
    {
        using var stream = File.OpenRead(file);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        State = Read(XDocument.Load(reader));
    }

    private static bool RenameFolderExists(string path, bool exactCase)
    {
        // File.Exists/Directory.Exists suppress access errors and cannot safely decide whether a destination is free.
        try { File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        return !exactCase || Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!)
            .Any(entry => string.Equals(Path.GetFileName(entry), Path.GetFileName(path), StringComparison.Ordinal));
    }

    private void RecoverRename()
    {
        XDocument pending;
        try
        {
            using var stream = File.OpenRead(file + ".rename.xml");
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            pending = XDocument.Load(reader);
        }
        catch (FileNotFoundException)
        {
            if (RequiresRenameRecovery) throw new IOException("The pending rename record is missing.");
            return;
        }
        RequiresRenameRecovery = true;
        var node = pending.Root ?? throw new InvalidDataException("Missing rename record.");
        if (node.Name != "Rename") throw new InvalidDataException("Unsupported rename record.");
        ValidateStructure(node, ["id", "root", "old", "new", "temporary"], []);
        var projectId = Guid.Parse((string)node.Attribute("id")!);
        var oldName = (string)node.Attribute("old")!;
        var newName = (string)node.Attribute("new")!;
        var temporaryName = (string)node.Attribute("temporary")!;
        ValidateName(oldName);
        ValidateName(newName);
        var project = State.Projects.FirstOrDefault(current => current.Id == projectId);
        var backupRoot = project?.BackupRootOverride ?? State.BackupRoot;
        var prefix = $".rename-{projectId:N}-";
        if (!temporaryName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(temporaryName[prefix.Length..], "N", out _) || oldName == newName ||
            !string.Equals((string)node.Attribute("root")!, backupRoot, StringComparison.Ordinal) ||
            State.Projects.Any(current => current.Id != projectId &&
                (string.Equals(current.Name, oldName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(current.Name, newName, StringComparison.OrdinalIgnoreCase))) ||
            State.Projects.Any(current => string.Equals(current.Name, temporaryName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The pending rename does not match this registry.");
        if (project is null || (project.Name != oldName && project.Name != newName))
            throw new InvalidDataException("The pending rename project does not match this registry.");
        var caseOnly = OperatingSystem.IsWindows() && string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        var candidates = new[] { oldName, newName, temporaryName }.Select(name => Path.Combine(backupRoot, name)).ToArray();
        foreach (var candidate in candidates) ValidateCopyPath(candidate, ItemType.Folder, true);
        var present = candidates.Where(candidate => RenameFolderExists(candidate, caseOnly)).ToArray();
        if (present.Length != 1)
            throw new IOException("Cannot locate one unambiguous backup folder. Preserved candidates: " + string.Join("; ", candidates));
        var destination = Path.Combine(backupRoot, project.Name);
        if (!string.Equals(present[0], destination, StringComparison.Ordinal))
        {
            ValidateCopyPath(present[0], ItemType.Folder, false);
            ValidateCopyPath(destination, ItemType.Folder, true);
            if (caseOnly && present[0] != candidates[2])
            {
                Directory.Move(present[0], candidates[2]);
                Directory.Move(candidates[2], destination);
            }
            else Directory.Move(present[0], destination);
        }
        File.Delete(file + ".rename.xml");
        RequiresRenameRecovery = false;
    }

    public void SetCopyConfirmation(bool enabled)
    {
        lock (gate)
        {
            EnsureIdle();
            Save(State with { Confirmations = State.Confirmations with { Copy = enabled } });
        }
    }

    public void SetRestoreConfirmation(bool enabled)
    {
        lock (gate)
        {
            EnsureIdle();
            Save(State with { Confirmations = State.Confirmations with { Restore = enabled } });
        }
    }

    public void SetMoveConfirmation(bool enabled)
    {
        lock (gate)
        {
            EnsureIdle();
            Save(State with { Confirmations = State.Confirmations with { Move = enabled } });
        }
    }

    private void EnsureIdle()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RequiresConfigurationReload) throw new InvalidOperationException("The saved configuration could not be confirmed. Restart the application before making changes.");
        if (RequiresRenameRecovery) throw new InvalidOperationException("A project rename needs recovery. Restart the application before making changes.");
        if (executionId is not null) throw new InvalidOperationException("An operation is already in progress. Changes are blocked until it finishes.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (executionId is not null) throw new InvalidOperationException("An operation is already in progress.");
            disposed = true;
            instanceLock.Dispose();
        }
    }

    private Project GetProject(Guid id) => State.Projects.FirstOrDefault(project => project.Id == id && project.Active)
        ?? throw new ArgumentException("Select an active project.", nameof(id));

    private Project UpdateItems(Project project, List<SelectedItem> items)
    {
        var updated = project with { Items = items.AsReadOnly() };
        var projects = Array.AsReadOnly(State.Projects.Select(current => current.Id == project.Id ? updated : current).ToArray());
        Save(State with { Projects = projects });
        return updated;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The item must have an absolute path.", nameof(path));
        try { XmlConvert.VerifyXmlChars(path); }
        catch (XmlException error)
        {
            throw new ArgumentException("The path contains invalid characters for the XML configuration.", nameof(path), error);
        }
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            // Avoid Win32 aliases (trailing dots/spaces, streams and device prefixes).
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("Select a standard file or folder path.", nameof(path));
            foreach (var component in normalized[Path.GetPathRoot(normalized)!.Length..].Split(Path.DirectorySeparatorChar))
                if (component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("The path contains an invalid or ambiguous component on Windows.", nameof(path));
        }
        return normalized;
    }

    private static void ValidateItem(SelectedItem item, IReadOnlyList<SelectedItem> existing, string backupRoot)
    {
        if (!Enum.IsDefined(item.Type)) throw new ArgumentException("Unknown item type.");
        if (ContainsPath(backupRoot, item.Path) ||
            (item.Type == ItemType.Folder && ContainsPath(item.Path, backupRoot)))
            throw new ArgumentException("The item matches the backup root, is inside it or contains it.");
        foreach (var existingItem in existing)
            if ((ContainsPath(existingItem.Path, item.Path) &&
                    (existingItem.Type == ItemType.Folder || ContainsPath(item.Path, existingItem.Path))) ||
                (item.Type == ItemType.Folder && ContainsPath(item.Path, existingItem.Path)))
                throw new ArgumentException($"The item duplicates or overlaps {existingItem.Path}.");
    }

    private static bool ContainsPath(string ancestor, string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var ancestorParts = ancestor.Split(Path.DirectorySeparatorChar);
        var pathParts = path.Split(Path.DirectorySeparatorChar);
        // The Linux root is an empty component; Windows/UNC roots retain their components.
        if (ancestor == Path.GetPathRoot(ancestor))
            return comparer.Equals(Path.GetPathRoot(ancestor), Path.GetPathRoot(path));
        return ancestorParts.Length <= pathParts.Length &&
            ancestorParts.Select((part, index) => comparer.Equals(part, pathParts[index])).All(equal => equal);
    }

    private void Save(Configuration proposal)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(file)!, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var projectsXml = new XElement("Projetos", proposal.Projects.Select(project => new XElement("Projeto",
                new XAttribute("id", project.Id), new XAttribute("nome", project.Name),
                new XAttribute("ativo", project.Active), project.LastOperation is null ? null :
                    new XAttribute("ultimoProcesso", project.LastOperation switch
                    {
                        ProjectOperation.Copy => "Copiar",
                        ProjectOperation.Move => "Mover",
                        ProjectOperation.Restore => "Restaurar",
                        _ => throw new InvalidOperationException("Unknown last operation.")
                    }), project.LastOperationStartedAt is null ? null :
                    new XAttribute("ultimoProcessoEm", project.LastOperationStartedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                project.SourceRoot is null ? null : new XAttribute("pastaOrigem", project.SourceRoot),
                project.BackupRootOverride is null ? null : new XAttribute("baseBackup", project.BackupRootOverride),
                new XElement("Itens", project.Items.Select(item =>
                    new XElement("Item", new XAttribute("tipo", item.Type == ItemType.File ? "Arquivo" : "Pasta"),
                        new XAttribute("caminho", item.Path)))))));
            var xml = new XDocument(new XElement("Configuracao",
                new XAttribute("versao", proposal.Version),
                new XElement("BaseBackup", proposal.BackupRoot),
                new XElement("Confirmacoes", new XAttribute("copiar", proposal.Confirmations.Copy),
                    new XAttribute("mover", proposal.Confirmations.Move),
                    new XAttribute("restaurar", proposal.Confirmations.Restore)), projectsXml));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                xml.Save(stream);
                stream.Flush(true);
            }
            replaceFile(temporary, file);
            State = proposal;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Could not save the configuration. " + error.Message, error);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Configuration Read(XDocument xml)
    {
        var root = xml.Root ?? throw new InvalidDataException("XML contains no configuration.");
        if (root.Name != "Configuracao" || (int?)root.Attribute("versao") != 1)
            throw new InvalidDataException("Unsupported configuration format.");
        ValidateStructure(root, ["versao"], ["BaseBackup", "Confirmacoes", "Projetos"]);
        var rootXml = root.Element("BaseBackup")!;
        if (rootXml.HasAttributes || rootXml.HasElements)
            throw new InvalidDataException("Unsupported backup root structure.");
        var backupRoot = NormalizePath(rootXml.Value);
        if (!Path.IsPathFullyQualified(backupRoot))
            throw new InvalidDataException("Invalid backup root.");
        var confirmations = root.Element("Confirmacoes") ?? throw new InvalidDataException("Missing confirmations.");
        ValidateStructure(confirmations, ["copiar", "mover", "restaurar"], []);
        var preferences = new Confirmations(
            (bool?)confirmations.Attribute("copiar") ?? throw new InvalidDataException("Missing Copy confirmation."),
            (bool?)confirmations.Attribute("mover") ?? throw new InvalidDataException("Missing Move confirmation."),
            (bool?)confirmations.Attribute("restaurar") ?? throw new InvalidDataException("Missing Restore confirmation."));
        var projectsXml = root.Element("Projetos") ?? throw new InvalidDataException("Missing projects.");
        if (projectsXml.HasAttributes || projectsXml.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
            throw new InvalidDataException("Unsupported project attributes.");
        var projects = new List<Project>();
        foreach (var node in projectsXml.Elements())
        {
            if (node.Name != "Projeto") throw new InvalidDataException("Unknown project record.");
            var attributes = new List<string> { "id", "nome", "ativo" };
            foreach (var optional in new[] { "ultimoProcesso", "ultimoProcessoEm", "pastaOrigem", "baseBackup" })
                if (node.Attribute(optional) is not null) attributes.Add(optional);
            ValidateStructure(node, attributes.ToArray(), ["Itens"]);
            var sourceRoot = node.Attribute("pastaOrigem") is { } sourceAttribute ? NormalizePath(sourceAttribute.Value) : null;
            var backupOverride = node.Attribute("baseBackup") is { } backupAttribute ? NormalizePath(backupAttribute.Value) : null;
            var lastOperation = (string?)node.Attribute("ultimoProcesso") switch
            {
                null => (ProjectOperation?)null,
                "Copiar" => ProjectOperation.Copy,
                "Mover" => ProjectOperation.Move,
                "Restaurar" => ProjectOperation.Restore,
                _ => throw new InvalidDataException("Unknown last operation.")
            };
            DateTimeOffset? lastOperationStartedAt = null;
            if (node.Attribute("ultimoProcessoEm") is { } startedAtAttribute)
            {
                if (lastOperation is null || !DateTimeOffset.TryParseExact(startedAtAttribute.Value, "O",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var startedAt) || startedAt.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Invalid last operation start time.");
                lastOperationStartedAt = startedAt;
            }
            var id = Guid.Parse((string?)node.Attribute("id") ?? "");
            var name = (string?)node.Attribute("nome") ?? "";
            ValidateName(name);
            if (id == Guid.Empty || projects.Any(project => project.Id == id ||
                string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Duplicate project identity or name.");
            var itemsXml = node.Element("Itens") ?? throw new InvalidDataException("Missing item list.");
            if (itemsXml.HasAttributes || itemsXml.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new InvalidDataException("Invalid item list.");
            var items = new List<SelectedItem>();
            foreach (var itemXml in itemsXml.Elements())
            {
                if (itemXml.Name != "Item") throw new InvalidDataException("Unknown item record.");
                ValidateStructure(itemXml, ["tipo", "caminho"], []);
                var typeText = (string?)itemXml.Attribute("tipo");
                if (typeText is not ("Arquivo" or "Pasta")) throw new InvalidDataException("Unknown item type.");
                var item = new SelectedItem(typeText == "Arquivo" ? ItemType.File : ItemType.Folder,
                    NormalizePath((string?)itemXml.Attribute("caminho") ?? ""));
                if (sourceRoot is not null && !ContainsPath(sourceRoot, item.Path))
                    throw new InvalidDataException("A registered item is outside its project's source folder.");
                ValidateItem(item, items, backupOverride ?? backupRoot);
                items.Add(item);
            }
            projects.Add(new Project(id, name,
                (bool?)node.Attribute("ativo") ?? throw new InvalidDataException("Missing active flag."),
                items.AsReadOnly())
            {
                LastOperation = lastOperation, LastOperationStartedAt = lastOperationStartedAt,
                SourceRoot = sourceRoot, BackupRootOverride = backupOverride
            });
        }
        var configuration = new Configuration(1, backupRoot, preferences, projects.AsReadOnly());
        foreach (var project in projects) ValidateProjectPaths(project, configuration);
        return configuration;
    }

    private static void ValidateStructure(XElement node, string[] attributes, string[] children)
    {
        if (node.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)) ||
            node.Attributes().Count() != attributes.Length ||
            node.Attributes().Any(attribute => !attributes.Contains(attribute.Name.ToString())) ||
            node.Elements().Count() != children.Length ||
            children.Any(child => node.Elements(child).Count() != 1))
            throw new InvalidDataException($"Unsupported {node.Name} structure. The document was preserved.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != name.Trim() ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/'))
            throw new ArgumentException("Enter a nonempty name valid for a subfolder, without leading or trailing spaces.", nameof(name));
        try { XmlConvert.VerifyXmlChars(name); }
        catch (XmlException error)
        {
            throw new ArgumentException("The name contains characters that cannot be saved in the XML configuration.", nameof(name), error);
        }
        if (OperatingSystem.IsWindows())
        {
            var stem = name.Split('.')[0].TrimEnd(' ');
            string[] reserved = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];
            if (name.EndsWith('.') || name.Length > 255 || reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("This name is reserved or invalid for a subfolder on Windows.", nameof(name));
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(name) > 255)
            throw new ArgumentException("The name exceeds the subfolder limit on Linux.", nameof(name));
    }
}
