using System.Globalization;
using System.Xml.Linq;
using LeRobocopier.Core;
using Xunit;

namespace LeRobocopier.Core.Tests;

public sealed class ProjectRegistryTests : IDisposable, IProgress<OperationProgress>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LeRobocopier-Tests", Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(directory, "settings.xml");
    private string BackupRoot => Path.Combine(directory, "Backups");
    private string SourceRoot => Directory.CreateDirectory(Path.Combine(directory, "Originals")).FullName;
    private Action<OperationProgress>? operationProgress;

    void IProgress<OperationProgress>.Report(OperationProgress value) => operationProgress?.Invoke(value);

    [Fact]
    public void Create_AfterReopening_PreservesRegistryAndPreferences()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Research & documentation", SourceRoot);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);

        var saved = Assert.Single(reopened.State.Projects);
        Assert.Equal(project.Id, saved.Id);
        Assert.Equal(project.Name, saved.Name);
        Assert.Equal(SourceRoot, saved.SourceRoot);
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
        registry.Create("Existing", SourceRoot);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => registry.Create(name, SourceRoot));

        Assert.Single(registry.State.Projects);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Create_WithNameReservedByInactiveProject_RejectsWithoutChangingRegistry()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Existing", SourceRoot);
        var xml = XDocument.Load(SettingsFile);
        xml.Root!.Element("Projetos")!.Element("Projeto")!.SetAttributeValue("ativo", false);
        xml.Root.Element("Confirmacoes")!.SetAttributeValue("copiar", false);
        xml.Save(SettingsFile);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => reopened.Create("EXISTING", SourceRoot));

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
        registry.Create("Existing", SourceRoot);
        var bytes = File.ReadAllBytes(SettingsFile);
        registry.Dispose();
        using var failure = new ProjectRegistry(SettingsFile, BackupRoot,
            (_, _) => throw new IOException("Simulated disk failure."));

        Assert.Throws<IOException>(() => failure.Create("New", SourceRoot));

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

        Assert.Throws<IOException>(() => registry.Create("New", SourceRoot));

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
            Assert.Throws<ArgumentException>(() => registry.Create(name, SourceRoot));
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
        var project = registry.Create("Research\\data", SourceRoot);
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
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Existing", SourceRoot);
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
        var error = Assert.Throws<ArgumentException>(() => registry.Create("Research\uFFFF", SourceRoot));
        Assert.Contains("XML configuration", error.Message);
        Assert.Empty(registry.State.Projects);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Open_WithUnknownRootAttribute_PreservesDocument()
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Existing", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var otherFolder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Other location")).FullName;
        var file = Path.Combine(otherFolder, "text & data.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "content");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<ArgumentException>(() => registry.AddItems(project.Id,
            [new(ItemType.Folder, folder), new(ItemType.Folder, Path.Combine(folder, "."))]));

        Assert.Empty(registry.State.Projects[0].Items);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void AddItems_WithSimilarNames_DistinguishesRootAndItemComponents()
    {
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var similar = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source-extra")).FullName;
        var similarRoot = Directory.CreateDirectory(Path.Combine(SourceRoot, "Backups-extra")).FullName;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);

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
        var project = registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var differentCase = Path.Combine(SourceRoot, "source");
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(differentCase);
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var file = Path.Combine(folder, "text.txt");
        File.WriteAllText(file, "original");
        Directory.CreateDirectory(BackupRoot);
        var copy = Path.Combine(BackupRoot, "copy.txt");
        File.WriteAllText(copy, "existing backup");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
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
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project", SourceRoot);
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
        var folder = Directory.CreateDirectory(Path.Combine(SourceRoot, "Source")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(folder, "Empty")).FullName;
        var file = Path.Combine(folder, "data.txt");
        File.WriteAllText(file, "original bytes");
        var modified = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(empty, modified);
        Directory.SetLastWriteTimeUtc(folder, modified);
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.AddItems(project.Id, [new(ItemType.Folder, folder)]);

        var preparation = registry.PrepareCopy(project.Id);
        Assert.Null(preparation.Failure);
        var result = registry.Copy(Assert.IsType<CopyPlan>(preparation.Plan));

        Assert.Null(result.Failure);
        Assert.True(result.Success);
        Assert.Single(result.VerifiedFiles);
        var copiedFolder = preparation.Plan!.Entries.Single(entry => entry.Source == folder).Destination;
        Assert.Equal(Path.Combine(BackupRoot, project.Name, "Source"), copiedFolder);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(copiedFolder, "data.txt")));
        Assert.True(Directory.Exists(Path.Combine(copiedFolder, "Empty")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(copiedFolder));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(copiedFolder, "Empty")));
        Assert.Equal("original bytes", File.ReadAllText(file));
    }

    [Fact]
    public void Directories_DefaultBackup_UsesBackupsSubfolderWithoutWritingToProfile()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "LeRobocopier", "Backups"), UserDirectories.BackupRoot());
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Open_WithNewDefault_PreservesSavedGlobalAndProjectRoots()
    {
        var custom = Path.Combine(directory, "Custom");
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project", SourceRoot);
        var xml = XDocument.Load(SettingsFile);
        xml.Descendants("Projeto").Single().SetAttributeValue("baseBackup", custom);
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);
        var newDefault = Path.Combine(directory, "NewDefault");

        using var reopened = new ProjectRegistry(SettingsFile, newDefault);
        Assert.Equal(BackupRoot, reopened.State.BackupRoot);
        Assert.Equal(custom, Assert.Single(reopened.State.Projects).BackupRootOverride);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.False(Directory.Exists(newDefault));
        Assert.False(Directory.Exists(custom));
        using var fresh = new ProjectRegistry(Path.Combine(directory, "fresh.xml"), newDefault);
        Assert.Equal(newDefault, fresh.State.BackupRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_WithFileAsSourceRoot_PreservesXmlAndReleasesLock(bool selected)
    {
        var source = SourceRoot;
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project", source);
        Directory.Delete(source);
        File.WriteAllText(source, "original bytes");
        var xml = XDocument.Load(SettingsFile);
        if (selected) xml.Descendants("Itens").Single().Add(new XElement("Item",
            new XAttribute("tipo", "Arquivo"), new XAttribute("caminho", source)));
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Equal("original bytes", File.ReadAllText(source));
        Assert.False(Directory.Exists(BackupRoot));
        using var releasedLock = new FileStream(SettingsFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(ProjectOperation.Copy)]
    [InlineData(ProjectOperation.Move)]
    [InlineData(ProjectOperation.Restore)]
    public void Prepare_WhenSourceFolderBecomesFile_ReportsSourceAndReleasesReservation(ProjectOperation operation)
    {
        var source = SourceRoot;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.Folder, source)]);
        Directory.Delete(source);
        File.WriteAllText(source, "preserve");
        var bytes = File.ReadAllBytes(SettingsFile);

        var preparation = Prepare(registry, project.Id, operation);
        Assert.Null(preparation.Plan);
        Assert.Equal(OperationStage.Preparing, preparation.Failure!.Stage);
        Assert.Equal(source, preparation.Failure.Path);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.False(File.Exists(Path.Combine(BackupRoot, project.Name)));
        Assert.False(Directory.Exists(BackupRoot));
        Assert.Equal("preserve", File.ReadAllText(source));
        File.Delete(source);
        Directory.CreateDirectory(source);
        registry.AbandonCopy(Assert.IsType<CopyPlan>(Prepare(registry, project.Id, operation).Plan));
    }

    [Theory]
    [InlineData(ProjectOperation.Copy)]
    [InlineData(ProjectOperation.Move)]
    [InlineData(ProjectOperation.Restore)]
    public void Execute_WhenSourceChangesAfterPreparation_StopsBeforeFileEffects(ProjectOperation operation)
    {
        var source = SourceRoot;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.Folder, source)]);
        var plan = Assert.IsType<CopyPlan>(Prepare(registry, project.Id, operation).Plan);
        Directory.Delete(source);
        File.WriteAllText(source, "preserve");

        var result = Execute(registry, plan, operation);
        Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
        Assert.Equal(source, result.Failure.Path);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.RemovedPaths);
        Assert.False(Directory.Exists(BackupRoot));
        Assert.Equal("preserve", File.ReadAllText(source));
        Assert.Equal(operation, registry.State.Projects[0].LastOperation);
        Assert.NotNull(registry.State.Projects[0].LastOperationStartedAt);
        Assert.Equal(operation switch
        {
            ProjectOperation.Copy => "Copiar",
            ProjectOperation.Move => "Mover",
            _ => "Restaurar"
        }, (string?)XDocument.Load(SettingsFile).Descendants("Projeto").Single().Attribute("ultimoProcesso"));
        File.Delete(source);
        Directory.CreateDirectory(source);
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_WithSourceRootSelected_AllowsReopeningAndRestoreWhenRootIsMissing(bool empty)
    {
        var source = SourceRoot;
        var original = Path.Combine(source, ".hidden");
        if (!empty) File.WriteAllText(original, "bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.Folder, source)]);
        var result = registry.Move(Assert.IsType<CopyPlan>(registry.PrepareMove(project.Id).Plan));
        Assert.Null(result.Failure);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(Path.Combine(BackupRoot, project.Name)));
        registry.Dispose();

        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(source, reopened.State.Projects[0].SourceRoot);
        var restored = reopened.Restore(Assert.IsType<CopyPlan>(reopened.PrepareRestore(project.Id).Plan));
        Assert.Null(restored.Failure);
        Assert.True(Directory.Exists(source));
        Assert.Equal(empty ? 0 : 1, Directory.GetFiles(source).Length);
        if (!empty) Assert.Equal("bytes", File.ReadAllText(original));
    }

    [Fact]
    public void Copy_LegacyProjectWithoutSourceRoot_PreservesAbsoluteMapping()
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "legacy");
        Guid id;
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot))
        {
            id = registry.Create("Legacy", SourceRoot).Id;
            registry.AddItems(id, [new(ItemType.File, file)]);
        }
        var xml = XDocument.Load(SettingsFile);
        xml.Descendants("Projeto").Single().Attribute("pastaOrigem")!.Remove();
        xml.Save(SettingsFile);
        using var legacy = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Null(legacy.State.Projects[0].SourceRoot);
        var plan = Assert.IsType<CopyPlan>(legacy.PrepareCopy(id).Plan);
        var root = Path.GetPathRoot(file)!;
        var rootName = OperatingSystem.IsWindows() ? root[..1] : "root";
        var destination = Path.Combine(BackupRoot, "Legacy", rootName, file[root.Length..]);
        Assert.Equal(destination, Assert.Single(plan.Entries).Destination);
        Assert.Null(legacy.Copy(plan).Failure);
        Assert.Equal("legacy", File.ReadAllText(destination));
        Assert.Throws<InvalidOperationException>(() => legacy.ChangeSourceRoot(id, SourceRoot));
    }

    [Fact]
    public void CopyAndRestore_WithExtras_PreserveUnselectedFilesAndBackup()
    {
        var source = SourceRoot;
        var file = Path.Combine(source, "data.txt");
        File.WriteAllText(file, "saved bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        var backupExtra = Path.Combine(backup, "extra.txt");
        File.WriteAllText(backupExtra, "backup extra");
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        Assert.Equal("saved bytes", File.ReadAllText(file));
        File.WriteAllText(file, "changed");
        var sourceExtra = Path.Combine(source, "extra.txt");
        File.WriteAllText(sourceExtra, "source extra");

        var preparation = registry.PrepareRestore(project.Id);
        Assert.Equal(file, Assert.Single(preparation.Plan!.Overwrites!));
        var result = registry.Restore(preparation.Plan);
        Assert.Null(result.Failure);
        Assert.Equal("saved bytes", File.ReadAllText(file));
        Assert.Equal("saved bytes", File.ReadAllText(Path.Combine(backup, "data.txt")));
        Assert.Equal("backup extra", File.ReadAllText(backupExtra));
        Assert.Equal("source extra", File.ReadAllText(sourceExtra));
        Assert.Single(result.VerifiedFiles);
    }

    [Fact]
    public void Restore_WhenNewOverwriteAppears_RequiresAnotherPreparation()
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "backup bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        File.Delete(file);
        var plan = Assert.IsType<CopyPlan>(registry.PrepareRestore(project.Id).Plan);
        Assert.Empty(plan.Overwrites!);
        File.WriteAllText(file, "new original");

        var result = registry.Restore(plan, disableConfirmation: true);
        Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
        Assert.Empty(result.WrittenPaths);
        Assert.Equal("new original", File.ReadAllText(file));
        Assert.Equal("backup bytes", File.ReadAllText(Path.Combine(BackupRoot, project.Name, "data.txt")));
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareRestore(project.Id).Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_WhenGlobalVerificationFails_PreservesEveryOriginal(bool corruptSource)
    {
        var source = SourceRoot;
        var first = Path.Combine(source, "first.txt");
        var second = Path.Combine(source, "second.txt");
        File.WriteAllText(first, "first bytes");
        File.WriteAllText(second, "second bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.File, first), new(ItemType.File, second)]);
        var plan = Assert.IsType<CopyPlan>(registry.PrepareMove(project.Id).Plan);
        var changed = false;
        operationProgress = value =>
        {
            if (changed || value.Stage != OperationStage.Verifying || value.Completed != 0) return;
            changed = true;
            var entry = plan.Entries.Single(entry => entry.Destination == value.Path);
            var target = corruptSource ? entry.Source : entry.Destination;
            var bytes = File.ReadAllBytes(target);
            bytes[0] ^= 1;
            File.WriteAllBytes(target, bytes);
        };

        var result = registry.Move(plan, progress: this);
        Assert.True(changed);
        Assert.Equal(OperationStage.Verifying, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, result.RemainingPaths.Count);
        Assert.Equal("second bytes", File.ReadAllText(second));
    }

    [Fact]
    public void Move_WhenNewChildAppearsBeforeRemoval_PreservesInventoriedAndNewContent()
    {
        var source = SourceRoot;
        var file = Path.Combine(source, "data.txt");
        var added = Path.Combine(source, "new.txt");
        File.WriteAllText(file, "bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.Folder, source)]);
        var plan = Assert.IsType<CopyPlan>(registry.PrepareMove(project.Id).Plan);
        operationProgress = value =>
        {
            if (value.Stage == OperationStage.Removing && !File.Exists(added)) File.WriteAllText(added, "new bytes");
        };

        var result = registry.Move(plan, progress: this);
        Assert.Equal(OperationStage.Removing, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal("bytes", File.ReadAllText(file));
        Assert.Equal("new bytes", File.ReadAllText(added));
        Assert.Contains(added, result.RemainingPaths);
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(BackupRoot, project.Name, "data.txt")));
    }

    [Fact]
    public void Copy_WithHiddenFileAndEmptyFolder_PreservesContentAndMetadata()
    {
        var source = SourceRoot;
        var file = Path.Combine(source, ".hidden");
        var empty = Directory.CreateDirectory(Path.Combine(source, "Empty")).FullName;
        File.WriteAllBytes(file, [0, 1, 2, 128, 255]);
        var modified = new DateTime(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        Directory.SetLastWriteTimeUtc(empty, modified);
        if (OperatingSystem.IsWindows()) File.SetAttributes(file, FileAttributes.Hidden);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(empty, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.SetLastWriteTimeUtc(source, modified);
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.Folder, source)]);

        var result = registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
        Assert.Null(result.Failure);
        var backup = Path.Combine(BackupRoot, project.Name);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(backup, ".hidden")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(backup, ".hidden")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(backup, "Empty")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(backup));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(backup, "Empty")));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(Path.Combine(backup, ".hidden")).HasFlag(FileAttributes.Hidden));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(File.GetUnixFileMode(file), File.GetUnixFileMode(Path.Combine(backup, ".hidden")));
            Assert.Equal(File.GetUnixFileMode(empty), File.GetUnixFileMode(Path.Combine(backup, "Empty")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_WithoutLastOperationTime_KeepsUnknownTimeThroughSave(bool hasOperation)
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project", SourceRoot);
        var xml = XDocument.Load(SettingsFile);
        if (hasOperation) xml.Descendants("Projeto").Single().SetAttributeValue("ultimoProcesso", "Copiar");
        xml.Save(SettingsFile);

        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = Assert.Single(reopened.State.Projects);
        Assert.Equal(hasOperation ? ProjectOperation.Copy : (ProjectOperation?)null, project.LastOperation);
        Assert.Null(project.LastOperationStartedAt);
        reopened.Rename(project.Id, "Renamed");
        Assert.Null(XDocument.Load(SettingsFile).Descendants("Projeto").Single().Attribute("ultimoProcessoEm"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("not-a-date", true)]
    [InlineData("2026-10-16", true)]
    [InlineData("2026-10-16T12:00:00.0000000", true)]
    [InlineData("2026-10-16T12:00:00.0000000+03:00", true)]
    [InlineData("2026-02-30T12:00:00.0000000+00:00", true)]
    [InlineData("2026-10-16T12:00:00.0000000+00:00", false)]
    public void Open_WithInvalidLastOperationTime_PreservesDocumentAndReleasesLock(string timestamp, bool hasOperation)
    {
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) registry.Create("Project", SourceRoot);
        var xml = XDocument.Load(SettingsFile);
        var project = xml.Descendants("Projeto").Single();
        if (hasOperation) project.SetAttributeValue("ultimoProcesso", "Copiar");
        project.SetAttributeValue("ultimoProcessoEm", timestamp);
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        using var releasedLock = new FileStream(SettingsFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Delete_WhenSavedStartTimeChanges_PreservesBackupAndBlocksMutations()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        registry.SetProjectActive(project.Id, false);
        var startedAt = Assert.IsType<DateTimeOffset>(registry.State.Projects[0].LastOperationStartedAt);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        var extra = Path.Combine(backup, "extra.txt");
        File.WriteAllText(extra, "preserve");
        var xml = XDocument.Load(SettingsFile);
        xml.Descendants("Projeto").Single().SetAttributeValue("ultimoProcessoEm", startedAt.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
        xml.Save(SettingsFile);
        var bytes = File.ReadAllBytes(SettingsFile);

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal("preserve", File.ReadAllText(extra));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Equal(startedAt, registry.State.Projects[0].LastOperationStartedAt);
        Assert.True(registry.RequiresConfigurationReload);
        Assert.Throws<InvalidOperationException>(() => registry.SetProjectActive(project.Id, true));
    }

    [Theory]
    [InlineData(ProjectOperation.Copy)]
    [InlineData(ProjectOperation.Move)]
    [InlineData(ProjectOperation.Restore)]
    public void Execute_WhenSavingFails_PreservesFilesPreferencesAndLastOperation(ProjectOperation operation)
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "bytes");
        Guid id;
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot))
        {
            id = registry.Create("Project", SourceRoot).Id;
            registry.AddItems(id, [new(ItemType.File, file)]);
            Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(id).Plan)).Failure);
        }
        var xml = File.ReadAllBytes(SettingsFile);
        using var failure = new ProjectRegistry(SettingsFile, BackupRoot, (_, _) => throw new IOException("Simulated failure."));
        var lastStartedAt = failure.State.Projects[0].LastOperationStartedAt;
        Assert.NotNull(lastStartedAt);
        var plan = Assert.IsType<CopyPlan>(Prepare(failure, id, operation).Plan);
        var result = Execute(failure, plan, operation, true);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal(xml, File.ReadAllBytes(SettingsFile));
        Assert.Equal(new Confirmations(), failure.State.Confirmations);
        Assert.Equal(ProjectOperation.Copy, failure.State.Projects[0].LastOperation);
        Assert.Equal(lastStartedAt, failure.State.Projects[0].LastOperationStartedAt);
        Assert.Equal("bytes", File.ReadAllText(file));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(BackupRoot, "Project", "data.txt")));
        failure.AbandonCopy(Assert.IsType<CopyPlan>(Prepare(failure, id, operation).Plan));
    }

    [Theory]
    [InlineData(ProjectOperation.Copy, "Copiar")]
    [InlineData(ProjectOperation.Move, "Mover")]
    [InlineData(ProjectOperation.Restore, "Restaurar")]
    public void Execute_RecordsAttemptAndConfirmationBeforeFileEffects(ProjectOperation operation, string persisted)
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "bytes");
        var saves = 0;
        var observe = false;
        DateTimeOffset? savedStartedAt = null;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (observe)
            {
                saves++;
                var xml = XDocument.Load(temporary);
                Assert.Equal(persisted, (string?)xml.Descendants("Projeto").Single().Attribute("ultimoProcesso"));
                savedStartedAt = DateTimeOffset.ParseExact((string)xml.Descendants("Projeto").Single().Attribute("ultimoProcessoEm")!,
                    "O", CultureInfo.InvariantCulture);
                Assert.Equal(TimeSpan.Zero, savedStartedAt.Value.Offset);
                Assert.False((bool)xml.Root!.Element("Confirmacoes")!.Attribute(operation switch
                {
                    ProjectOperation.Copy => "copiar", ProjectOperation.Move => "mover", _ => "restaurar"
                })!);
                Assert.Equal("bytes", File.ReadAllText(file));
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        var abandoned = Assert.IsType<CopyPlan>(Prepare(registry, project.Id, operation).Plan);
        registry.AbandonCopy(abandoned);
        Assert.Null(registry.State.Projects[0].LastOperation);
        Assert.Null(registry.State.Projects[0].LastOperationStartedAt);
        observe = true;
        var before = DateTimeOffset.UtcNow;
        var result = Execute(registry, Assert.IsType<CopyPlan>(Prepare(registry, project.Id, operation).Plan), operation, true);
        var after = DateTimeOffset.UtcNow;
        Assert.Null(result.Failure);
        Assert.Equal(1, saves);
        var startedAt = Assert.IsType<DateTimeOffset>(registry.State.Projects[0].LastOperationStartedAt);
        Assert.InRange(startedAt, before, after);
        Assert.Equal(savedStartedAt, startedAt);
        Assert.Equal(operation, registry.State.Projects[0].LastOperation);
        Assert.False(operation switch
        {
            ProjectOperation.Copy => registry.State.Confirmations.Copy,
            ProjectOperation.Move => registry.State.Confirmations.Move,
            _ => registry.State.Confirmations.Restore
        });
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(operation, reopened.State.Projects[0].LastOperation);
        Assert.Equal(savedStartedAt, reopened.State.Projects[0].LastOperationStartedAt);
    }

    [Theory]
    [InlineData(ProjectOperation.Copy)]
    [InlineData(ProjectOperation.Move)]
    [InlineData(ProjectOperation.Restore)]
    public void Execute_WithNoItems_RecordsAttemptThroughRenameAndReactivation(ProjectOperation operation)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var plan = Assert.IsType<CopyPlan>(Prepare(registry, project.Id, operation).Plan);
        Assert.Empty(plan.Entries);

        var result = Execute(registry, plan, operation);
        Assert.Null(result.Failure);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.RemovedPaths);
        Assert.False(result.Success);
        Assert.Equal(operation, registry.State.Projects[0].LastOperation);
        var startedAt = Assert.IsType<DateTimeOffset>(registry.State.Projects[0].LastOperationStartedAt);
        Assert.Equal(operation, registry.Rename(project.Id, "Renamed").LastOperation);
        Assert.Equal(operation, registry.SetProjectActive(project.Id, false).LastOperation);
        Assert.Equal(operation, registry.SetProjectActive(project.Id, true).LastOperation);
        Assert.Equal(startedAt, registry.State.Projects[0].LastOperationStartedAt);
        Assert.False(Directory.Exists(BackupRoot));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var saved = Assert.Single(reopened.State.Projects);
        Assert.Equal(project.Id, saved.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.True(saved.Active);
        Assert.Equal(operation, saved.LastOperation);
        Assert.Equal(startedAt, saved.LastOperationStartedAt);
    }

    [Fact]
    public void Delete_WithActiveOrUnknownProject_PreservesOriginalsAndBackup()
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        var bytes = File.ReadAllBytes(SettingsFile);

        Assert.Throws<InvalidOperationException>(() => registry.Delete(project.Id));
        Assert.Throws<ArgumentException>(() => registry.Delete(Guid.NewGuid()));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Equal("original", File.ReadAllText(Path.Combine(BackupRoot, project.Name, "data.txt")));
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_WithInactiveProject_RemovesOnlyItsEntireBackupAndRegistration(bool withBackup)
    {
        var file = Path.Combine(SourceRoot, "original.txt");
        File.WriteAllText(file, "original");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        var backup = Path.Combine(BackupRoot, project.Name);
        var other = Directory.CreateDirectory(Path.Combine(BackupRoot, "Other")).FullName;
        File.WriteAllText(Path.Combine(other, "unrelated.txt"), "unrelated");
        if (withBackup)
        {
            Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
            Directory.CreateDirectory(Path.Combine(backup, "Empty"));
            File.WriteAllText(Path.Combine(backup, "extra.txt"), "extra");
        }
        registry.SetProjectActive(project.Id, false);

        var result = registry.Delete(project.Id);
        Assert.Null(result.Failure);
        Assert.True(result.ProjectDeleted);
        Assert.True(result.Success);
        Assert.Empty(result.RemainingPaths);
        Assert.Equal(withBackup ? 4 : 0, result.RemovedPaths.Count);
        Assert.False(Directory.Exists(backup));
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(other, "unrelated.txt")));
        Assert.Empty(registry.State.Projects);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Empty(reopened.State.Projects);
    }

    [Fact]
    public void Delete_WhenPreliminarySaveFails_PreservesBackupAndReservedName()
    {
        var fail = false;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (fail) throw new IOException("Simulated failure.");
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        var extra = Path.Combine(backup, "extra.txt");
        File.WriteAllText(extra, "extra");
        registry.SetProjectActive(project.Id, false);
        var xml = File.ReadAllBytes(SettingsFile);
        fail = true;

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.False(result.ProjectDeleted);
        Assert.Equal("extra", File.ReadAllText(extra));
        Assert.Equal(xml, File.ReadAllBytes(SettingsFile));
        Assert.Equal(project.Id, Assert.Single(registry.State.Projects).Id);
        Assert.Throws<ArgumentException>(() => registry.Create("PROJECT", SourceRoot));
    }

    [Fact]
    public void Delete_WhenConfigurationIsInsideTarget_PreservesConfigurationAndLock()
    {
        var settings = Path.Combine(BackupRoot, "Project", "settings.xml");
        using var registry = new ProjectRegistry(settings, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        var xml = File.ReadAllBytes(settings);

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal(xml, File.ReadAllBytes(settings));
        Assert.True(File.Exists(settings + ".lock"));
        Assert.Single(registry.State.Projects);
    }

    [Fact]
    public void Delete_WithLinkedDirectory_PreservesExternalContent()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        var external = Directory.CreateDirectory(Path.Combine(directory, "OutsideBackup")).FullName;
        var file = Path.Combine(external, "preserve.txt");
        File.WriteAllText(file, "external bytes");
        var link = Path.Combine(backup, "Linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/d /c mklink /J \"{link}\" \"{external}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        else Directory.CreateSymbolicLink(link, external);

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal("external bytes", File.ReadAllText(file));
        Assert.True(Directory.Exists(link));
        Assert.Single(registry.State.Projects);
        Directory.Delete(link, false);
    }

    [Fact]
    public void Delete_WhenRemovalPartiallyFails_ReportsFilesystemAndReservesName()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        File.WriteAllText(Path.Combine(backup, "first-long-name.txt"), "first");
        File.WriteAllText(Path.Combine(backup, "second.txt"), "second");
        operationProgress = value =>
        {
            if (value.Stage == OperationStage.Removing && value.Completed == 1)
                throw new IOException("Deterministic interruption after one removal.");
        };

        var result = registry.Delete(project.Id, this);
        Assert.Equal(OperationStage.Removing, result.Failure!.Stage);
        Assert.Single(result.RemovedPaths);
        Assert.All(result.RemovedPaths, path => Assert.False(File.Exists(path) || Directory.Exists(path)));
        Assert.All(result.RemainingPaths, path => Assert.True(File.Exists(path) || Directory.Exists(path)));
        Assert.Equal(2, result.RemainingPaths.Count);
        Assert.False(result.ProjectDeleted);
        Assert.Throws<ArgumentException>(() => registry.Create("PROJECT", SourceRoot));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(project.Id, Assert.Single(reopened.State.Projects).Id);
        Assert.False(reopened.State.Projects[0].Active);
    }

    [Fact]
    public void Delete_WhenNewContentAppearsBeforeRemovingFolder_PreservesItAndRegistration()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        var inventoried = Path.Combine(backup, "old.txt");
        var added = Path.Combine(backup, "new.txt");
        File.WriteAllText(inventoried, "old bytes");
        operationProgress = value =>
        {
            if (value.Stage == OperationStage.Removing && value.Path == backup)
                File.WriteAllText(added, "new bytes");
        };

        var result = registry.Delete(project.Id, this);
        Assert.Equal(OperationStage.Removing, result.Failure!.Stage);
        Assert.Equal(inventoried, Assert.Single(result.RemovedPaths));
        Assert.False(File.Exists(inventoried));
        Assert.Equal("new bytes", File.ReadAllText(added));
        Assert.Contains(backup, result.RemainingPaths);
        Assert.Contains(added, result.RemainingPaths);
        Assert.False(result.ProjectDeleted);
        Assert.False(Assert.Single(registry.State.Projects).Active);
        Assert.Throws<ArgumentException>(() => registry.Create("PROJECT", SourceRoot));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(project.Id, Assert.Single(reopened.State.Projects).Id);
        Assert.False(reopened.State.Projects[0].Active);
        Assert.Equal("new bytes", File.ReadAllText(added));
        Assert.Null(reopened.Delete(project.Id).Failure);
        Assert.False(Directory.Exists(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_WhenFinalSaveReportsFailure_FollowsPersistedDecision(bool replaced)
    {
        var deleting = false;
        var saves = 0;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (deleting && ++saves == 2)
            {
                if (replaced) File.Move(temporary, destination, true);
                throw new IOException("Final save reported failure.");
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        var backup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        File.WriteAllText(Path.Combine(backup, "extra.txt"), "extra");
        deleting = true;

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Equal(replaced, result.ProjectDeleted);
        Assert.False(Directory.Exists(backup));
        Assert.Empty(result.RemainingPaths);
        Assert.Equal(replaced ? 0 : 1, registry.State.Projects.Count);
        Assert.False(registry.RequiresConfigurationReload);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(replaced ? 0 : 1, reopened.State.Projects.Count);
    }

    [Fact]
    public void Delete_WhenFinalSaveCannotBeResolved_BlocksFurtherMutations()
    {
        var deleting = false;
        var saves = 0;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (deleting && ++saves == 2)
            {
                File.WriteAllText(destination, "<Broken");
                throw new IOException("Inconclusive final save.");
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        registry.SetProjectActive(project.Id, false);
        deleting = true;

        var result = registry.Delete(project.Id);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.True(registry.RequiresConfigurationReload);
        Assert.Throws<InvalidOperationException>(() => registry.Create("Other", SourceRoot));
        Assert.Throws<InvalidOperationException>(() => registry.SetProjectActive(project.Id, true));
        Assert.Single(registry.State.Projects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rename_WhenSavingReportsFailure_RecoversAccordingToPersistedName(bool replaced)
    {
        var failing = false;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (failing)
            {
                if (replaced) File.Move(temporary, destination, true);
                throw new IOException("Simulated rename save failure.");
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        registry.SetProjectActive(project.Id, false);
        var oldBackup = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        File.WriteAllText(Path.Combine(oldBackup, "extra.txt"), "extra bytes");
        failing = true;

        Assert.Throws<IOException>(() => registry.Rename(project.Id, "Renamed"));
        var name = replaced ? "Renamed" : "Project";
        var saved = Assert.Single(registry.State.Projects);
        Assert.Equal(project.Id, saved.Id);
        Assert.Equal(name, saved.Name);
        Assert.False(saved.Active);
        Assert.Equal(ProjectOperation.Copy, saved.LastOperation);
        Assert.Equal("extra bytes", File.ReadAllText(Path.Combine(BackupRoot, name, "extra.txt")));
        Assert.False(Directory.Exists(Path.Combine(BackupRoot, replaced ? "Project" : "Renamed")));
        Assert.False(File.Exists(SettingsFile + ".rename.xml"));
        Assert.False(registry.RequiresRenameRecovery);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        var reopenedProject = Assert.Single(reopened.State.Projects);
        Assert.Equal(saved.Id, reopenedProject.Id);
        Assert.Equal(saved.Name, reopenedProject.Name);
        Assert.Equal(saved.Active, reopenedProject.Active);
        Assert.Equal(saved.LastOperation, reopenedProject.LastOperation);
        Assert.Equal(saved.LastOperationStartedAt, reopenedProject.LastOperationStartedAt);
        Assert.Equal(saved.Items, reopenedProject.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_WithPendingRename_UsesPersistedNameOrPreservesAmbiguousCandidates(bool ambiguous)
    {
        Guid id;
        using (var registry = new ProjectRegistry(SettingsFile, BackupRoot)) id = registry.Create("Project", SourceRoot).Id;
        var temporary = $".rename-{id:N}-{Guid.NewGuid():N}";
        var moved = Directory.CreateDirectory(Path.Combine(BackupRoot, "Renamed")).FullName;
        File.WriteAllText(Path.Combine(moved, "extra.txt"), "bytes");
        if (ambiguous) Directory.CreateDirectory(Path.Combine(BackupRoot, temporary));
        new XDocument(new XElement("Rename", new XAttribute("id", id), new XAttribute("root", BackupRoot),
            new XAttribute("old", "Project"), new XAttribute("new", "Renamed"), new XAttribute("temporary", temporary)))
            .Save(SettingsFile + ".rename.xml");
        var xml = File.ReadAllBytes(SettingsFile);

        if (ambiguous)
        {
            Assert.Throws<InvalidDataException>(() => new ProjectRegistry(SettingsFile, BackupRoot));
            Assert.True(Directory.Exists(moved));
            Assert.True(Directory.Exists(Path.Combine(BackupRoot, temporary)));
            Assert.True(File.Exists(SettingsFile + ".rename.xml"));
            using var releasedLock = new FileStream(SettingsFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        else
        {
            using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
            Assert.Equal(id, reopened.State.Projects[0].Id);
            Assert.Equal("Project", reopened.State.Projects[0].Name);
            Assert.Equal("bytes", File.ReadAllText(Path.Combine(BackupRoot, "Project", "extra.txt")));
            Assert.False(Directory.Exists(moved));
            Assert.False(File.Exists(SettingsFile + ".rename.xml"));
        }
        Assert.Equal(xml, File.ReadAllBytes(SettingsFile));
    }

    [Fact]
    public void Migration_WithInheritedAndCustomProjects_MigratesOnlySelectedBackups()
    {
        var file = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(file, "bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var active = registry.Create("Active", SourceRoot);
        var inactive = registry.Create("Inactive", SourceRoot);
        var custom = registry.Create("Custom", SourceRoot);
        var customRoot = Path.Combine(directory, "CustomBase");
        Assert.Null(registry.Migrate(Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(custom.Id, customRoot).Plan)).Failure);
        foreach (var project in new[] { active, inactive, custom })
        {
            registry.AddItems(project.Id, [new(ItemType.File, file)]);
            Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
            var root = project.Id == custom.Id ? customRoot : BackupRoot;
            File.WriteAllText(Path.Combine(root, project.Name, "extra.txt"), "extra");
        }
        registry.SetProjectActive(inactive.Id, false);
        var startedTimes = registry.State.Projects.ToDictionary(project => project.Id, project => project.LastOperationStartedAt);
        var next = Path.Combine(directory, "NextBase");
        var unrelated = Path.Combine(BackupRoot, "unrelated.txt");
        File.WriteAllText(unrelated, "outside projects");

        var result = registry.Migrate(Assert.IsType<CopyPlan>(registry.PrepareMigration(next).Plan));
        Assert.Null(result.Failure);
        Assert.True(result.BackupRootChanged);
        Assert.Equal(next, registry.State.BackupRoot);
        foreach (var project in new[] { active, inactive })
        {
            Assert.False(Directory.Exists(Path.Combine(BackupRoot, project.Name)));
            Assert.Equal("bytes", File.ReadAllText(Path.Combine(next, project.Name, "data.txt")));
            Assert.Equal("extra", File.ReadAllText(Path.Combine(next, project.Name, "extra.txt")));
        }
        Assert.Equal("outside projects", File.ReadAllText(unrelated));
        Assert.False(Directory.Exists(Path.Combine(next, custom.Name)));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(customRoot, custom.Name, "data.txt")));
        Assert.Equal(customRoot, registry.State.Projects.Single(project => project.Id == custom.Id).BackupRootOverride);
        Assert.All(registry.State.Projects, project => Assert.Equal(ProjectOperation.Copy, project.LastOperation));
        Assert.All(registry.State.Projects, project => Assert.Equal(startedTimes[project.Id], project.LastOperationStartedAt));
        Assert.False(registry.State.Projects.Single(project => project.Id == inactive.Id).Active);

        var own = Path.Combine(directory, "OwnBase");
        Assert.Null(registry.Migrate(Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(active.Id, own).Plan)).Failure);
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(own, active.Name, "data.txt")));
        Assert.False(Directory.Exists(Path.Combine(next, active.Name)));
        Assert.True(Directory.Exists(Path.Combine(next, inactive.Name)));
        Assert.Equal(next, registry.State.BackupRoot);
        Assert.Null(registry.Migrate(Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(active.Id, null).Plan)).Failure);
        Assert.Null(registry.State.Projects.Single(project => project.Id == active.Id).BackupRootOverride);
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(next, active.Name, "data.txt")));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(next, reopened.State.BackupRoot);
        Assert.Equal(new[] { active.Id, inactive.Id, custom.Id }, reopened.State.Projects.Select(project => project.Id));
        Assert.All(reopened.State.Projects, project => Assert.Equal(startedTimes[project.Id], project.LastOperationStartedAt));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProjectMigration_WithoutBackup_SavesDestinationWithoutFileEffects(bool hasItems, bool hasRecordedEmptyAttempt)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        if (hasRecordedEmptyAttempt)
            Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        var lastOperation = registry.State.Projects[0].LastOperation;
        var startedAt = registry.State.Projects[0].LastOperationStartedAt;
        var original = Path.Combine(SourceRoot, "data.txt");
        File.WriteAllText(original, "original bytes");
        if (hasItems) registry.AddItems(project.Id, [new(ItemType.File, original)]);
        var next = Path.Combine(directory, "NextBase");
        var plan = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        Assert.Empty(plan.Entries);
        Assert.Empty(plan.Warnings);

        var result = registry.Migrate(plan);
        Assert.Null(result.Failure);
        Assert.True(result.BackupRootChanged);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.VerifiedFiles);
        Assert.Empty(result.RemovedPaths);
        Assert.Empty(result.Warnings);
        Assert.Equal(next, registry.State.Projects[0].BackupRootOverride);
        Assert.Equal(BackupRoot, registry.State.BackupRoot);
        Assert.Equal(lastOperation, registry.State.Projects[0].LastOperation);
        Assert.Equal(startedAt, registry.State.Projects[0].LastOperationStartedAt);
        Assert.False(Directory.Exists(next));
        Assert.False(Directory.Exists(BackupRoot));
        Assert.Equal("original bytes", File.ReadAllText(original));
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(next, reopened.State.Projects[0].BackupRootOverride);
        Assert.Equal(project.Id, reopened.State.Projects[0].Id);
        Assert.Equal(lastOperation, reopened.State.Projects[0].LastOperation);
        Assert.Equal(startedAt, reopened.State.Projects[0].LastOperationStartedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectMigration_WithManualBackupAndNoRecordedRun_InventoriesExistingBackup(bool hasFile)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var old = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        if (hasFile) File.WriteAllText(Path.Combine(old, "manual.txt"), "manual bytes");
        var next = Path.Combine(directory, "NextBase");
        var plan = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        Assert.NotEmpty(plan.Entries);
        Assert.Contains(plan.Entries, entry => entry.Source == old && entry.Type == ItemType.Folder);
        Assert.Null(registry.State.Projects[0].LastOperation);
        Assert.Null(registry.State.Projects[0].LastOperationStartedAt);

        var result = registry.Migrate(plan);
        Assert.Null(result.Failure);
        Assert.True(Directory.Exists(Path.Combine(next, project.Name)));
        Assert.False(Directory.Exists(old));
        if (hasFile) Assert.Equal("manual bytes", File.ReadAllText(Path.Combine(next, project.Name, "manual.txt")));
        Assert.Null(registry.State.Projects[0].LastOperation);
        Assert.Null(registry.State.Projects[0].LastOperationStartedAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProjectMigration_WhenBackupAppearsAfterEmptyPreparation_RequiresNewPreparation(bool duringSavingCallback, bool replacePublicEntries)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var next = Path.Combine(directory, "NextBase");
        var plan = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        Assert.Empty(plan.Entries);
        var bytes = File.ReadAllBytes(SettingsFile);
        var old = Path.Combine(BackupRoot, project.Name);
        var invoked = false;
        if (duringSavingCallback)
            operationProgress = update =>
            {
                if (update.Stage != OperationStage.Saving) return;
                invoked = true;
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "appeared.txt"), "preserve bytes");
            };
        else
        {
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "appeared.txt"), "preserve bytes");
        }

        var submitted = replacePublicEntries ? plan with
        {
            Entries = [new CopyEntry(ItemType.Folder, old, Path.Combine(next, project.Name))]
        } : plan;
        var result = registry.Migrate(submitted, this);
        Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
        Assert.Contains("Prepare migration again", result.Failure.Message);
        Assert.Equal(duringSavingCallback, invoked);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.RemovedPaths);
        Assert.False(result.BackupRootChanged);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Null(registry.State.Projects[0].BackupRootOverride);
        Assert.Equal("preserve bytes", File.ReadAllText(Path.Combine(old, "appeared.txt")));
        Assert.False(Directory.Exists(next));
        operationProgress = null;
        var retry = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        Assert.NotEmpty(retry.Entries);
        registry.AbandonCopy(retry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectMigration_WithoutBackup_WhenSavingReportsFailure_FollowsSavedDecisionWithoutFileEffects(bool replaced)
    {
        var failing = false;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (failing)
            {
                if (replaced) File.Move(temporary, destination, true);
                throw new IOException("Simulated destination save failure.");
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        var next = Path.Combine(directory, "NextBase");
        var plan = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        var bytes = File.ReadAllBytes(SettingsFile);
        failing = true;

        var result = registry.Migrate(plan);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Equal(replaced, result.BackupRootChanged);
        Assert.Equal(replaced ? next : null, registry.State.Projects[0].BackupRootOverride);
        Assert.Empty(result.WrittenPaths);
        Assert.Empty(result.RemovedPaths);
        Assert.False(Directory.Exists(next));
        Assert.False(Directory.Exists(BackupRoot));
        Assert.False(registry.RequiresConfigurationReload);
        if (!replaced) Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Null(registry.State.Projects[0].LastOperationStartedAt);
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(replaced ? next : null, reopened.State.Projects[0].BackupRootOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectMigration_WithoutBackup_WhenDestinationIsOccupied_PreservesConfiguration(bool afterPreparation)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var next = Path.Combine(directory, "NextBase");
        CopyPlan? plan = afterPreparation ? Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan) : null;
        var occupied = Directory.CreateDirectory(Path.Combine(next, project.Name)).FullName;
        File.WriteAllText(Path.Combine(occupied, "preserve.txt"), "occupied bytes");
        var bytes = File.ReadAllBytes(SettingsFile);

        if (plan is not null)
        {
            var result = registry.Migrate(plan);
            Assert.Equal(OperationStage.Preparing, result.Failure!.Stage);
            Assert.Empty(result.WrittenPaths);
            Assert.Empty(result.RemovedPaths);
        }
        else Assert.NotNull(registry.PrepareProjectMigration(project.Id, next).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsFile));
        Assert.Null(registry.State.Projects[0].BackupRootOverride);
        Assert.Equal("occupied bytes", File.ReadAllText(Path.Combine(occupied, "preserve.txt")));
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
    }

    [Fact]
    public void ProjectMigration_WithSameEffectiveBase_ChangesInheritanceWithoutChangingRecordedAttempt()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        var startedAt = registry.State.Projects[0].LastOperationStartedAt;
        var custom = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, BackupRoot).Plan);
        Assert.Empty(custom.Entries);
        Assert.Null(registry.Migrate(custom).Failure);
        Assert.Equal(BackupRoot, registry.State.Projects[0].BackupRootOverride);
        var inherited = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, null).Plan);
        Assert.Empty(inherited.Entries);
        Assert.Null(registry.Migrate(inherited).Failure);
        Assert.Null(registry.State.Projects[0].BackupRootOverride);
        Assert.Equal(ProjectOperation.Copy, registry.State.Projects[0].LastOperation);
        Assert.Equal(startedAt, registry.State.Projects[0].LastOperationStartedAt);
        Assert.False(Directory.Exists(BackupRoot));
        var next = Path.Combine(directory, "NextBase");
        var missingBackup = Assert.IsType<CopyPlan>(registry.PrepareProjectMigration(project.Id, next).Plan);
        Assert.Empty(missingBackup.Entries);
        Assert.Null(registry.Migrate(missingBackup).Failure);
        Assert.Equal(startedAt, registry.State.Projects[0].LastOperationStartedAt);
        Assert.Equal(ProjectOperation.Copy, registry.State.Projects[0].LastOperation);
        Assert.False(Directory.Exists(next));
    }

    [Fact]
    public void GlobalMigration_WithoutBackup_PreservesMissingBackupWarning()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        registry.Create("Project", SourceRoot);
        var plan = Assert.IsType<CopyPlan>(registry.PrepareMigration(Path.Combine(directory, "NextBase")).Plan);
        Assert.Empty(plan.Entries);
        Assert.NotEmpty(plan.Warnings);
        Assert.Equal(Guid.Empty, plan.ProjectId);
        registry.AbandonCopy(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_WhenSavingReportsFailure_PreservesOldBackupAndFollowsSavedBase(bool replaced)
    {
        var failing = false;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (failing)
            {
                if (replaced) File.Move(temporary, destination, true);
                throw new IOException("Simulated migration save failure.");
            }
            File.Move(temporary, destination, true);
        });
        var project = registry.Create("Project", SourceRoot);
        var old = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        File.WriteAllText(Path.Combine(old, "extra.txt"), "bytes");
        var next = Path.Combine(directory, "Next");
        var plan = Assert.IsType<CopyPlan>(registry.PrepareMigration(next).Plan);
        failing = true;

        var result = registry.Migrate(plan);
        Assert.Equal(OperationStage.Saving, result.Failure!.Stage);
        Assert.Equal(replaced, result.BackupRootChanged);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(old, "extra.txt")));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(next, project.Name, "extra.txt")));
        Assert.Equal(replaced ? next : BackupRoot, registry.State.BackupRoot);
        Assert.Null(registry.State.Projects[0].LastOperation);
        Assert.Null(registry.State.Projects[0].LastOperationStartedAt);
        Assert.False(registry.RequiresConfigurationReload);
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(replaced ? next : BackupRoot, reopened.State.BackupRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_WhenDestinationIsOccupied_NeverMergesOrRemovesOldBackup(bool afterPreparation)
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var old = Directory.CreateDirectory(Path.Combine(BackupRoot, project.Name)).FullName;
        File.WriteAllText(Path.Combine(old, "extra.txt"), "old bytes");
        var next = Path.Combine(directory, "Next");
        CopyPlan? plan = afterPreparation ? Assert.IsType<CopyPlan>(registry.PrepareMigration(next).Plan) : null;
        var occupied = Directory.CreateDirectory(Path.Combine(next, project.Name)).FullName;
        File.WriteAllText(Path.Combine(occupied, "unrelated.txt"), "preserve");

        if (plan is not null) Assert.NotNull(registry.Migrate(plan).Failure);
        else Assert.NotNull(registry.PrepareMigration(next).Failure);
        Assert.Equal(BackupRoot, registry.State.BackupRoot);
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(old, "extra.txt")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(occupied, "unrelated.txt")));
        Assert.False(File.Exists(Path.Combine(occupied, "extra.txt")));
        registry.AbandonCopy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan));
    }

    [Fact]
    public void ChangeSourceRoot_PreservesRelativePathsAndOriginals_ThenRestoresToNewRoot()
    {
        var source = SourceRoot;
        var child = Directory.CreateDirectory(Path.Combine(source, "Sub")).FullName;
        var file = Path.Combine(child, "data.txt");
        File.WriteAllText(file, "backup bytes");
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        Assert.Null(registry.Copy(Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan)).Failure);
        var next = Directory.CreateDirectory(Path.Combine(directory, "NewSource")).FullName;

        var startedAt = registry.State.Projects[0].LastOperationStartedAt;
        var changed = registry.ChangeSourceRoot(project.Id, next);
        Assert.Equal(startedAt, changed.LastOperationStartedAt);
        var target = Path.Combine(next, "Sub", "data.txt");
        Assert.Equal(project.Id, changed.Id);
        Assert.Equal(target, Assert.Single(changed.Items).Path);
        Assert.Equal(ProjectOperation.Copy, changed.LastOperation);
        Assert.False(File.Exists(target));
        Assert.Equal("backup bytes", File.ReadAllText(file));
        Assert.Null(registry.Restore(Assert.IsType<CopyPlan>(registry.PrepareRestore(project.Id).Plan)).Failure);
        Assert.Equal("backup bytes", File.ReadAllText(target));
        Assert.Equal("backup bytes", File.ReadAllText(file));
        registry.Dispose();
        using var reopened = new ProjectRegistry(SettingsFile, BackupRoot);
        Assert.Equal(next, reopened.State.Projects[0].SourceRoot);
        Assert.Equal(target, Assert.Single(reopened.State.Projects[0].Items).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangeSourceRoot_WhenSavingReportsFailure_FollowsPersistedDecision(bool replaced)
    {
        var failing = false;
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot, (temporary, destination) =>
        {
            if (failing)
            {
                if (replaced) File.Move(temporary, destination, true);
                throw new IOException("Simulated source save failure.");
            }
            File.Move(temporary, destination, true);
        });
        var source = SourceRoot;
        var file = Path.Combine(source, "data.txt");
        File.WriteAllText(file, "original");
        var project = registry.Create("Project", source);
        registry.AddItems(project.Id, [new(ItemType.File, file)]);
        var next = Directory.CreateDirectory(Path.Combine(directory, "NewSource")).FullName;
        failing = true;

        Assert.Throws<IOException>(() => registry.ChangeSourceRoot(project.Id, next));
        Assert.Equal(replaced ? next : source, registry.State.Projects[0].SourceRoot);
        Assert.Equal(Path.Combine(replaced ? next : source, "data.txt"), Assert.Single(registry.State.Projects[0].Items).Path);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(next, "data.txt")));
        Assert.False(registry.RequiresConfigurationReload);
    }

    [Fact]
    public void PreparedOperation_BlocksMutationsCompetingExecutionAndDisposal_ThenReleases()
    {
        using var registry = new ProjectRegistry(SettingsFile, BackupRoot);
        var project = registry.Create("Project", SourceRoot);
        var plan = Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan);

        Assert.Throws<InvalidOperationException>(() => registry.Create("Other", SourceRoot));
        Assert.Throws<InvalidOperationException>(() => registry.Rename(project.Id, "Renamed"));
        Assert.Throws<InvalidOperationException>(() => registry.SetProjectActive(project.Id, false));
        Assert.Throws<InvalidOperationException>(() => registry.ChangeSourceRoot(project.Id, SourceRoot));
        Assert.Throws<InvalidOperationException>(() => registry.SetCopyConfirmation(false));
        Assert.Throws<InvalidOperationException>(() => registry.AddItems(project.Id, []));
        Assert.Throws<InvalidOperationException>(() => registry.PrepareMove(project.Id));
        Assert.Throws<InvalidOperationException>(() => registry.PrepareMigration(Path.Combine(directory, "Next")));
        Assert.Throws<InvalidOperationException>(() => registry.Restore(plan));
        Assert.Throws<InvalidOperationException>(() => registry.Copy(plan with { ProjectId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => registry.Dispose());
        registry.AbandonCopy(plan);
        Assert.Throws<InvalidOperationException>(() => registry.Copy(plan));
        var nextPlan = Assert.IsType<CopyPlan>(registry.PrepareCopy(project.Id).Plan);
        Assert.Null(registry.Copy(nextPlan).Failure);
        Assert.Equal("Renamed", registry.Rename(project.Id, "Renamed").Name);
    }

    private static CopyPreparation Prepare(ProjectRegistry registry, Guid id, ProjectOperation operation) => operation switch
    {
        ProjectOperation.Copy => registry.PrepareCopy(id),
        ProjectOperation.Move => registry.PrepareMove(id),
        _ => registry.PrepareRestore(id)
    };

    private static CopyResult Execute(ProjectRegistry registry, CopyPlan plan, ProjectOperation operation, bool disableConfirmation = false) => operation switch
    {
        ProjectOperation.Copy => registry.Copy(plan, disableConfirmation),
        ProjectOperation.Move => registry.Move(plan, disableConfirmation),
        _ => registry.Restore(plan, disableConfirmation)
    };

    public void Dispose()
    {
        // Only the random directory created by this test; never use the scripts' source paths.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
