using System.Xml;
using System.Xml.Linq;

namespace LeRobocopier.Core;

// Changes become visible only after replacing the XML file on disk.
public sealed class ProjectRegistry
{
    private readonly string file;
    private readonly Action<string, string> replaceFile;

    public Configuration State { get; private set; }

    public ProjectRegistry(string file, string defaultRoot,
        Action<string, string>? replaceFile = null)
    {
        this.file = Path.GetFullPath(file);
        this.replaceFile = replaceFile ?? ((temporary, destination) => File.Move(temporary, destination, true));
        // File.Exists also returns false on access errors; opening the file protects unreadable state.
        try
        {
            using var stream = File.OpenRead(this.file);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            State = Read(XDocument.Load(reader));
        }
        catch (FileNotFoundException)
        {
            State = Initial(defaultRoot);
        }
        catch (DirectoryNotFoundException)
        {
            State = Initial(defaultRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException
            or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Could not read {this.file}. The existing file was preserved.", error);
        }
    }

    public Project Create(string name)
    {
        ValidateName(name);
        if (State.Projects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("An active or inactive project already has this name.", nameof(name));

        var project = new Project(Guid.NewGuid(), name, true, Array.Empty<SelectedItem>());
        var projects = Array.AsReadOnly(State.Projects.Append(project).ToArray());
        Save(State with { Projects = projects });
        return project;
    }

    private static Configuration Initial(string defaultRoot)
    {
        if (!Path.IsPathFullyQualified(defaultRoot))
            throw new ArgumentException("The backup root must have an absolute path.", nameof(defaultRoot));
        return new Configuration(1, NormalizePath(defaultRoot), new Confirmations(), Array.Empty<Project>());
    }

    public Project AddItems(Guid projectId, IReadOnlyList<SelectedItem> selection)
    {
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

    public Project RemoveItem(Guid projectId, SelectedItem item)
    {
        var project = GetProject(projectId);
        if (!project.Items.Contains(item)) throw new ArgumentException("The item does not belong to this project.", nameof(item));
        return UpdateItems(project, project.Items.Where(registered => registered != item).ToList());
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
            throw new IOException("Could not save the project. The previous registry was preserved.", error);
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
