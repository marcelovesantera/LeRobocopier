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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException
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

    public Project Create(string name)
    {
        lock (gate)
        {
            EnsureIdle();
            ValidateName(name);
            if (State.Projects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active or inactive project already has this name.", nameof(name));

            var project = new Project(Guid.NewGuid(), name, true, Array.Empty<SelectedItem>());
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
                ValidateItem(item, items, State.BackupRoot);
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

            var source = Path.Combine(State.BackupRoot, project.Name);
            var destination = Path.Combine(State.BackupRoot, name);
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
            var temporaryFolder = Path.Combine(State.BackupRoot, temporaryName);
            ValidateCopyPath(temporaryFolder, ItemType.Folder, true);
            if (RenameFolderExists(temporaryFolder, false) || State.Projects.Any(current =>
                string.Equals(current.Name, temporaryName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The temporary rename destination already exists.");
            var pending = new XDocument(new XElement("Rename", new XAttribute("id", projectId),
                new XAttribute("root", State.BackupRoot), new XAttribute("old", project.Name),
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
        var prefix = $".rename-{projectId:N}-";
        if (!temporaryName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(temporaryName[prefix.Length..], "N", out _) || oldName == newName ||
            !string.Equals((string)node.Attribute("root")!, State.BackupRoot, StringComparison.Ordinal) ||
            State.Projects.Any(current => current.Id != projectId &&
                (string.Equals(current.Name, oldName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(current.Name, newName, StringComparison.OrdinalIgnoreCase))) ||
            State.Projects.Any(current => string.Equals(current.Name, temporaryName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The pending rename does not match this registry.");
        var project = State.Projects.FirstOrDefault(current => current.Id == projectId);
        if (project is null || (project.Name != oldName && project.Name != newName))
            throw new InvalidDataException("The pending rename project does not match this registry.");
        var caseOnly = OperatingSystem.IsWindows() && string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        var candidates = new[] { oldName, newName, temporaryName }.Select(name => Path.Combine(State.BackupRoot, name)).ToArray();
        foreach (var candidate in candidates) ValidateCopyPath(candidate, ItemType.Folder, true);
        var present = candidates.Where(candidate => RenameFolderExists(candidate, caseOnly)).ToArray();
        if (present.Length != 1)
            throw new IOException("Cannot locate one unambiguous backup folder. Preserved candidates: " + string.Join("; ", candidates));
        var destination = Path.Combine(State.BackupRoot, project.Name);
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
                new XAttribute("ativo", project.Active), new XElement("Itens", project.Items.Select(item =>
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
            ValidateStructure(node, ["id", "nome", "ativo"], ["Itens"]);
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
                ValidateItem(item, items, backupRoot);
                items.Add(item);
            }
            projects.Add(new Project(id, name,
                (bool?)node.Attribute("ativo") ?? throw new InvalidDataException("Missing active flag."),
                items.AsReadOnly()));
        }
        return new Configuration(1, backupRoot, preferences, projects.AsReadOnly());
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
