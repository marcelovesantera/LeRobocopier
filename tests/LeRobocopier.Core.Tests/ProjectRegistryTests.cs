using System.Xml.Linq;
using LeRobocopier.Core;
using Xunit;

namespace LeRobocopier.Core.Tests;

public sealed class ProjectRegistryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LeRobocopier-Tests", Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(directory, "settings.xml");
    private string BackupRoot => Path.Combine(directory, "Backups");

    [Fact]
    public void Create_AfterReopening_PreservesRegistryAndPreferences()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Research & documentation");
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);

        var saved = Assert.Single(reopened.State.Projects);
        Assert.Equal(project.Id, saved.Id);
        Assert.Equal(project.Name, saved.Name);
        Assert.True(saved.Active);
        Assert.Empty(saved.Items);
        Assert.Equal(1, reopened.State.Version);
        Assert.Equal(BackupRoot, reopened.State.BackupRoot);
        Assert.Equal(new Confirmations(true, true, true), reopened.State.Confirmations);
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("name/child")]
    [InlineData(" name")]
    public void Create_WithInvalidName_PreservesDiskAndMemory(string name)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        registry.Create("Existing");
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => registry.Create(name));

        Assert.Single(registry.State.Projects);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Create_WithNameReservedByInactiveProject_RejectsWithoutChangingRegistry()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Existing");
        var xml = XDocument.Load(SettingsFile);
        xml.Root!.Element("Projetos")!.Element("Projeto")!.SetAttributeValue("ativo", false);
        xml.Root.Element("Confirmacoes")!.SetAttributeValue("copiar", false);
        xml.Save(SettingsFile);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => reopened.Create("EXISTING"));

        Assert.Equal(project.Id, Assert.Single(reopened.State.Projects).Id);
        Assert.False(reopened.State.Projects[0].Active);
        Assert.False(reopened.State.Confirmations.Copy);
        Assert.True(reopened.State.Confirmations.Move);
        Assert.True(reopened.State.Confirmations.Restore);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Create_WhenReplacementFails_PreservesLastPersistedState()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        registry.Create("Existing");
        var bytes = File.ReadAllBytes(SettingsFile);
        registry.Dispose();
        using var failure = new ProjectRegistry(SettingsFile, BackupRoot,
            (_, _) => throw new IOException("Simulated disk failure."));

        Assert.Throws<IOException>(() => failure.Create("New"));

        Assert.Single(failure.State.Projects);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        failure.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Single(reopened.State.Projects);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Create_WhenFirstWriteFails_DoesNotPublishProject()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot,
            (_, _) => throw new UnauthorizedAccessException("Simulated access denial."));

        Assert.Throws<IOException>(() => registry.Create("New"));

        Assert.Empty(registry.State.Projects);
        Assert.False(File.Exists(SettingsFile));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("<Broken XML")]
    [InlineData("<Configuracao versao=\"99\" />")]
    [InlineData("<!DOCTYPE x [<!ENTITY text 'value'>]><Configuracao versao=\"1\" />")]
    public void Open_WithUnreadableXml_PreservesBytesAndPreventsOverwrite(string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsFile, content);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));

        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Single(Directory.GetFiles(directory, "*.xml"));
        using var releasedLock = new FileStream(SettingsFile + ".lock", FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Create_WithReservedWindowsName_RejectsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        foreach (var name in new[] { "CON", "CON .txt", "nul.txt", "COM1", "LPT².txt", "name.", "name:child", "name\\child" })
            Assert.Throws<ArgumentException>(() => registry.Create(name));
        Assert.Empty(registry.State.Projects);
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    public void Directories_WithoutAbsoluteXdg_UsesConfigurationInUserFolder(string? xdg)
    {
        var path = UserDirectories.Configuration(false, directory, Path.Combine(directory, "Local"), xdg);
        Assert.Equal(Path.Combine(directory, ".config", "LeRobocopier", "settings.xml"), path);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Directories_WithUserContext_RespectsXdgAndWindows()
    {
        var xdg = Path.Combine(directory, "xdg");
        var local = Path.Combine(directory, "Local");
        Assert.Equal(Path.Combine(xdg, "LeRobocopier", "settings.xml"),
            UserDirectories.Configuration(false, directory, local, xdg));
        Assert.Equal(Path.Combine(local, "LeRobocopier", "settings.xml"),
            UserDirectories.Configuration(true, directory, local, xdg));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Create_WithLinuxBackslash_PreservesName()
    {
        if (OperatingSystem.IsWindows()) return;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Research\\data");
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(project.Name, Assert.Single(reopened.State.Projects).Name);
    }

    [Theory]
    [InlineData("BaseBackup")]
    [InlineData("Projetos")]
    [InlineData("Unknown")]
    public void Open_WithUnknownStructure_PreservesDocument(string element)
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Existing");
        var xml = XDocument.Load(SettingsFile);
        xml.Root!.Add(new XElement(element));
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);
        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Create_WithInvalidXmlCharacter_RejectsWithClearMessage()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var error = Assert.Throws<ArgumentException>(() => registry.Create("Research\uFFFF"));
        Assert.Contains("XML configuration", error.Message);
        Assert.Empty(registry.State.Projects);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Open_WithUnknownRootAttribute_PreservesDocument()
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Existing");
        var xml = XDocument.Load(SettingsFile);
        xml.Root!.Element("BaseBackup")!.SetAttributeValue("source", "unknown");
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);
        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void AddItems_AfterReopening_PreservesTypesPathsAndDynamicFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var otherFolder = Directory.CreateDirectory(Path.Combine(directory, "Other location")).FullName;
        var file = Path.Combine(otherFolder, "text & data.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        registry.AddItems(project.Id, [new(ItemType.Folder, folder + Path.DirectorySeparatorChar),
            new(ItemType.File, Path.Combine(otherFolder, ".", "text & data.txt"))]);
        File.WriteAllText(Path.Combine(folder, "new.txt"), "new content");

        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var saved = Assert.Single(reopened.State.Projects);
        Assert.Equal(project.Id, saved.Id);
        Assert.Equal(new[] { new SelectedItem(ItemType.Folder, folder), new SelectedItem(ItemType.File, file) }, saved.Items);
        Assert.Equal(2, XDocument.Load(SettingsFile).Descendants("Item").Count());
        Assert.DoesNotContain("new.txt", File.ReadAllText(SettingsFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddItems_WithOverlapInBothDirections_PreservesXmlAndMemory(bool folderFirst)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "content");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        var folderItem = new SelectedItem(ItemType.Folder, folder);
        var fileItem = new SelectedItem(ItemType.File, file);
        registry.AddItems(project.Id, [folderFirst ? folderItem : fileItem]);
        var state = registry.State;
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => registry.AddItems(project.Id, [folderFirst ? fileItem : folderItem]));

        Assert.Same(state, registry.State);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void AddItems_WithDuplicateInBatch_DoesNotPublishValidPart()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => registry.AddItems(project.Id,
            [new(ItemType.Folder, folder), new(ItemType.Folder, Path.Combine(folder, "."))]));

        Assert.Empty(registry.State.Projects[0].Items);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void AddItems_WithSimilarNames_DistinguishesRootAndItemComponents()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var similar = Directory.CreateDirectory(Path.Combine(directory, "Source-extra")).FullName;
        var similarRoot = Directory.CreateDirectory(BackupRoot + "-extra").FullName;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");

        registry.AddItems(project.Id, [new(ItemType.Folder, folder), new(ItemType.Folder, similar), new(ItemType.Folder, similarRoot)]);

        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(3, reopened.State.Projects[0].Items.Count);
    }

    [Fact]
    public void AddItems_AtRootBoundaries_RejectsRootDescendantsAndAncestors()
    {
        Directory.CreateDirectory(BackupRoot);
        var subfolder = Directory.CreateDirectory(Path.Combine(BackupRoot, "subfolder")).FullName;
        var file = Path.Combine(BackupRoot, "file.txt");
        File.WriteAllText(file, "backup");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        var bytes = File.ReadAllBytes(SettingsFile);

        foreach (var item in new[] { new SelectedItem(ItemType.Folder, BackupRoot), new(ItemType.Folder, subfolder),
            new(ItemType.File, file), new(ItemType.Folder, directory), new(ItemType.Folder, Path.GetPathRoot(directory)!) })
            Assert.Throws<ArgumentException>(() => registry.AddItems(project.Id, [item]));

        Assert.Empty(registry.State.Projects[0].Items);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Equal("backup", File.ReadAllText(file));
    }

    [Fact]
    public void AddItems_WithCaseDifference_RespectsWindowsAndLinux()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var differentCase = Path.Combine(directory, "source");
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(differentCase);
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        registry.AddItems(project.Id, [new(ItemType.Folder, folder)]);
        var bytes = File.ReadAllBytes(SettingsFile);

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() => registry.AddItems(project.Id, [new(ItemType.Folder, differentCase)]));
            Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        }
        else
        {
            registry.AddItems(project.Id, [new(ItemType.Folder, differentCase)]);
            registry.Dispose();
            using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
            Assert.Equal(2, reopened.State.Projects[0].Items.Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangeItems_WhenPersistenceFails_PreservesRegistryAndOriginals(bool remove)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        var item = new SelectedItem(ItemType.File, file);
        if (remove) registry.AddItems(project.Id, [item]);
        var bytes = File.ReadAllBytes(SettingsFile);
        registry.Dispose();
        using var failure = new ProjectRegistry(SettingsFile, BackupRoot, (_, _) => throw new IOException("Simulated failure."));
        var state = failure.State;

        Assert.Throws<IOException>(() => { if (remove) failure.RemoveItem(project.Id, item); else failure.AddItems(project.Id, [item]); });

        Assert.Same(state, failure.State);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void RemoveItem_PreservesOriginalsAndBackupAndAcceptsMissingSourceOnRead()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "original");
        Directory.CreateDirectory(BackupRoot);
        var copy = Path.Combine(BackupRoot, "copy.txt");
        File.WriteAllText(copy, "existing backup");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        var item = new SelectedItem(ItemType.File, file);
        registry.AddItems(project.Id, [item]);
        var missing = file + ".temporary";
        File.Move(file, missing);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(item, Assert.Single(reopened.State.Projects[0].Items));
        File.Move(missing, file);

        reopened.RemoveItem(project.Id, item);

        reopened.Dispose();
        using var saved = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Empty(saved.State.Projects[0].Items);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Equal("existing backup", File.ReadAllText(copy));
    }

    [Theory]
    [InlineData("Unknown", "absolute")]
    [InlineData("Arquivo", "relative")]
    public void Open_WithInvalidItem_PreservesDocument(string type, string path)
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project");
        var xml = XDocument.Load(SettingsFile);
        xml.Descendants("Itens").Single().Add(new XElement("Item", new XAttribute("tipo", type),
            new XAttribute("caminho", path == "absolute" ? Path.Combine(directory, "missing.txt") : path)));
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));

        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Copy_WithFolder_PreservesBytesTimestampsAndEmptyDirectories()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Source")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(folder, "Empty")).FullName;
        var file = Path.Combine(folder, "data.txt");
        File.WriteAllText(file, "original bytes");
        var modified = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(empty, modified);
        Directory.SetLastWriteTimeUtc(folder, modified);
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project");
        registry.AddItems(project.Id, [new(ItemType.Folder, folder)]);

        var preparation = registry.PrepareCopy(project.Id);
        Assert.Null(preparation.Failure);
        var result = registry.Copy(Assert.IsType<CopyPlan>(preparation.Plan));

        Assert.Null(result.Failure);
        Assert.True(result.Success);
        Assert.Single(result.VerifiedFiles);
        var copiedFolder = preparation.Plan!.Entries.Single(entry => entry.Source == folder).Destination;
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(copiedFolder, "data.txt")));
        Assert.True(Directory.Exists(Path.Combine(copiedFolder, "Empty")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(copiedFolder));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(copiedFolder, "Empty")));
        Assert.Equal("original bytes", File.ReadAllText(file));
    }

    public void Dispose()
    {
        // Only the random directory created by this test; never use the scripts' source paths.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
