using FileOrganizer.Core;
using FileOrganizer.Windows;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class ConfigurationEditorTests
{
    [WindowsFact]
    public void RoundTripPreservesBytesRawPathsCommentsAndOmittedDefaults()
    {
        using Fixture f = new();
        string text = f.Text().Replace("patterns = [\"*.txt\"]", "# keep this\npatterns = [\n '*.txt', # and this\n]\ncase_sensitive=false");
        ConfigurationDocument document = new(text);
        Assert.Equal(text, document.Render());
        Assert.False(document.IsDirty);
        document.Folders[0].Rules[0].Fields["name"].Text = "Renamed";
        string edited = document.Render();
        Assert.Contains("# keep this", edited); Assert.Contains("# and this", edited);
        Assert.Contains("destination = '" + f.Destination + "'", edited);
        Assert.DoesNotContain("max_parallel_moves", edited);
        Assert.Equal("Renamed", ConfigurationLoader.Parse(edited).Folders[0].Rules[0].Name);
    }
    [WindowsFact]
    public void EveryFieldCanBeEditedAndMissingSectionsAreCreated()
    {
        using Fixture f = new();
        ConfigurationDocument d = new(f.Text());
        d.General.Fields["scan_on_startup"].Flag = false;
        d.General.Fields["watch_config_changes"].Flag = false;
        d.General.Fields["dry_run"].Flag = true;
        d.General.Fields["log_level"].Text = "debug";
        d.General.Fields["file_ready_timeout_seconds"].Text = "5";
        d.General.Fields["max_parallel_moves"].Text = "3";
        ConfigurationSection folder = d.Folders[0];
        folder.Fields["recursive"].Flag = true;
        folder.Fields["duplicates.name_collision"].Text = "skip";
        folder.Fields["duplicates.identical_content"].Text = "delete";
        folder.Fields["duplicates.copy_suffix"].Text = "_other_{n}";
        ConfigurationSection rule = folder.Rules[0];
        rule.Fields["extensions"].Text = ".txt\n.PDF";
        rule.Fields["patterns"].Text = "*hello*\n*gemini*";
        rule.Fields["exclude_patterns"].Text = "*_draft*";
        rule.Fields["case_sensitive"].Flag = true;
        rule.Fields["destination"].Text = "relative";
        Configuration result = d.Validate();
        Assert.Equal(new General(false, false, true, LogLevel.Debug, 5, 3), result.General);
        Assert.Equal(new DuplicatePolicy(NameCollision.Skip, IdenticalContent.Delete, "_other_{n}"), result.Folders[0].Duplicates);
        Assert.True(result.Folders[0].Recursive);
        Assert.Equal(2, result.Folders[0].Rules[0].Extensions.Length);
        Assert.Equal(Path.Combine(f.Source, "relative"), result.Folders[0].Rules[0].Destination);
        Assert.Contains("destination = \"relative\"", d.Render());
    }
    [WindowsFact]
    public void DottedKeysInlineTablesAndQuotedKeysSurviveEditing()
    {
        using Fixture f = new();
        string text = $$"""
            version = 1
            general = {dry_run=true, log_level='info'} # inline general
            [[folders]]
            name='first'
            path='{{f.Source}}'
            duplicates={identical_content='skip'}
            [["folders"."rules"]]
            name='first rule'
            destination='{{f.Destination}}'
            patterns=['*.txt']
            """;
        ConfigurationDocument d = new(text);
        d.General.Fields["max_parallel_moves"].Text = "4";
        d.General.Fields["watch_config_changes"].Flag = false;
        d.Folders[0].Fields["duplicates.name_collision"].Text = "skip";
        d.Folders[0].Rules[0].Fields["case_sensitive"].Flag = true;
        Configuration result = d.Validate();
        Assert.Equal(4, result.General.ParallelMoves); Assert.False(result.General.WatchConfigChanges);
        Assert.Equal(NameCollision.Skip, result.Folders[0].Duplicates.NameCollision);
        Assert.Contains("# inline general", d.Render());
        string dotted = f.Text().Replace("[general]\n", "").Replace("file_ready_timeout_seconds = 2", "general.file_ready_timeout_seconds = 2");
        ConfigurationDocument e = new(dotted); e.General.Fields["dry_run"].Flag = true;
        Assert.True(e.Validate().General.DryRun);
    }
    [WindowsFact]
    public void AddRemoveReorderAndDuplicateRetainRuleSettings()
    {
        using Fixture f = new();
        ConfigurationDocument d = new(f.Text(rule: "# rule comment\nexclude_patterns=['*_draft*']"));
        ConfigurationSection original = d.Folders[0].Rules[0];
        ConfigurationSection copy = d.DuplicateRule(d.Folders[0], original);
        copy.Fields["patterns"].Text = "*.pdf";
        d.Folders[0].Rules.Move(1, 0);
        Configuration result = d.Validate();
        Assert.EndsWith("(copy)", result.Folders[0].Rules[0].Name);
        Assert.Equal("*_draft*", result.Folders[0].Rules[0].Exclusions[0]);
        Assert.Equal(2, d.Render().Split("# rule comment").Length - 1);
        d.Folders[0].Rules.Remove(original);
        Assert.Single(d.Validate().Folders[0].Rules);
        ConfigurationSection second = d.AddFolder();
        second.Fields["name"].Text = "second";
        second.Fields["path"].Text = Path.Combine(f.Root, "disabled");
        second.Rules[0].Fields["patterns"].Text = "*.exe";
        second.Rules[0].Fields["destination"].Text = "Executables";
        d.Folders.Move(1, 0);
        result = d.Validate();
        Assert.Equal("second", result.Folders[0].Name); Assert.False(result.Folders[0].Enabled);
        d.Folders.Remove(second); Assert.Single(d.Validate().Folders);
    }
    [WindowsFact]
    public void NewDocumentAndGeneralWithoutATableAreSupported()
    {
        using Fixture f = new();
        ConfigurationDocument d = new("version = 1\n");
        d.General.Fields["dry_run"].Flag = true;
        ConfigurationSection folder = d.AddFolder();
        folder.Fields["path"].Text = f.Source; folder.Fields["enabled"].Flag = true;
        folder.Rules[0].Fields["destination"].Text = f.Destination;
        folder.Rules[0].Fields["extensions"].Text = ".txt";
        Assert.True(d.Validate().General.DryRun);
    }
    [WindowsFact]
    public void UnknownPropertiesAndTablesAreNeverDiscarded()
    {
        using Fixture f = new();
        string text = f.Text() + "\n[metadata]\nfuture = 1.234\nwhen=2026-10-15\n";
        ConfigurationDocument d = new(text);
        d.Folders[0].Fields["name"].Text = "modified";
        Assert.Contains("future = 1.234", d.Render()); Assert.Contains("when=2026-10-15", d.Render());
        Assert.Throws<ConfigException>(() => d.Validate());
        d.Folders.Clear(); Assert.Contains("[metadata]", d.Render());
    }
    [WindowsFact]
    public void InvalidDraftDoesNotSaveOrCreateFolders()
    {
        using Fixture f = new();
        string original = f.Text(); File.WriteAllText(f.ConfigPath, original);
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open();
        d.Folders[0].Rules[0].Fields["destination"].Text = f.Source;
        Assert.Throws<ConfigException>(() => file.Save(d.Render()));
        Assert.Equal(original, File.ReadAllText(f.ConfigPath)); Assert.False(Directory.Exists(f.Destination));
    }
    [WindowsFact]
    public void AtomicSaveHasBackupAndRejectsExternalEditsIncludingCommitRace()
    {
        using Fixture f = new();
        string original = f.Text(); File.WriteAllText(f.ConfigPath, original);
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open();
        d.General.Fields["dry_run"].Flag = true;
        file.Save(d.Render());
        Assert.True(ConfigurationLoader.Load(f.ConfigPath).General.DryRun);
        Assert.Equal(original, File.ReadAllText(f.ConfigPath + ".bak"));
        d = file.Open(); d.General.Fields["dry_run"].Flag = false;
        string external = original + "\n# external edit\n";
        Assert.Throws<ConfigurationConflictException>(() => file.Save(d.Render(), () => File.WriteAllText(f.ConfigPath, external)));
        Assert.Equal(external, File.ReadAllText(f.ConfigPath));
        Assert.Empty(Directory.GetFiles(f.Root, ".fileorganizer-config-*.tmp"));
        Assert.Throws<ConfigurationConflictException>(() => file.Save(d.Render()));
        ConfigurationFile other = new(f.ConfigPath);
        Assert.Throws<ConfigurationConflictException>(() => other.Save(original));
    }
    [WindowsFact]
    public void FailedCommitPreservesOriginalAndCleansOwnedTemporaryFile()
    {
        using Fixture f = new();
        File.WriteAllText(f.ConfigPath, f.Text());
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open(); d.General.Fields["dry_run"].Flag = true;
        Assert.Throws<IOException>(() => file.Save(d.Render(), () => throw new IOException("injected")));
        Assert.Equal(f.Text(), File.ReadAllText(f.ConfigPath));
        Assert.Empty(Directory.GetFiles(f.Root, ".fileorganizer-config-*.tmp"));
    }
    [WindowsFact]
    public void UnchangedUtf8BomAndCrLfStayByteIdentical()
    {
        using Fixture f = new();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(f.Text().Replace("\r\n", "\n").Replace("\n", "\r\n"))];
        File.WriteAllBytes(f.ConfigPath, bytes);
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open(); file.Save(d.Render());
        Assert.Equal(bytes, File.ReadAllBytes(f.ConfigPath));
        d.General.Fields["dry_run"].Flag = true; file.Save(d.Render());
        Assert.True(File.ReadAllBytes(f.ConfigPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }
    [WindowsFact]
    public async Task RepositoryConfigurationsRoundTripAndAreAcceptedByCli()
    {
        using Fixture f = new();
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string variable = "FILEORGANIZER_GUI_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, f.Source);
        try
        {
            foreach (string name in new[] { "Config.Example.toml", "Config.Downloads.toml" })
            {
                string path = Path.Combine(repository, name);
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path).Replace("%USERPROFILE%\\Downloads", "%" + variable + "%");
                ConfigurationDocument d = new(text);
                Assert.Equal(text, d.Render());
                d.General.Fields["dry_run"].Flag = true;
                string output = Path.Combine(f.Root, name);
                new ConfigurationFile(output).Save(d.Render());
                Assert.Contains("%" + variable + "%", File.ReadAllText(output));
                Assert.Contains("'Images'", File.ReadAllText(output));
                Assert.Equal(0, await Program.Main(["validate", "--config", output]));
                Configuration result = ConfigurationLoader.Load(output);
                if (name == "Config.Downloads.toml") Assert.Equal("AI Images", Matching.First(result.Folders[0], "ChatGPT image.png")!.Name);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
    [WindowsFact]
    public async Task RunningEngineReloadsAtomicEditorSave()
    {
        using Fixture f = new();
        File.WriteAllText(f.ConfigPath, f.Text(general: "scan_on_startup=false"));
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task running = host.RunAsync(stop.Token);
        try
        {
            ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open();
            d.General.Fields["dry_run"].Flag = true; file.Save(d.Render());
            await Fixture.Eventually(() => host.Current.General.DryRun);
            Assert.Equal(0, host.RejectedConfigurations);
        }
        finally { stop.Cancel(); await running; }
    }
    [WindowsFact]
    public void SavePreservesRestrictedDaclOnConfigurationAndBackup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture f = new(); File.WriteAllText(f.ConfigPath, f.Text());
        FileInfo info = new(f.ConfigPath);
        FileSecurity security = info.GetAccessControl(AccessControlSections.Access);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.WriteData, AccessControlType.Deny));
        info.SetAccessControl(security);
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open(); d.General.Fields["dry_run"].Flag = true;
        file.Save(d.Render());
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(f.ConfigPath, "corrupt"));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(f.ConfigPath + ".bak", "corrupt"));
        Assert.True(ConfigurationLoader.Load(f.ConfigPath).General.DryRun);
    }
    [WindowsFact]
    public void CaseInsensitiveEnumValuesDisplayWithoutChangingOriginalBytes()
    {
        using Fixture f = new();
        string text = f.Text("identical_content='COPY'", "log_level='INFO'");
        ConfigurationDocument d = new(text);
        Assert.Equal("copy", d.Folders[0].Fields["duplicates.identical_content"].Text);
        Assert.Equal("info", d.General.Fields["log_level"].Text);
        Assert.Equal(text, d.Render());
    }
    [WindowsFact]
    public void OwnedTemporaryCannotBeModifiedOrSubstitutedDuringCommit()
    {
        using Fixture f = new(); File.WriteAllText(f.ConfigPath, f.Text());
        ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open(); d.General.Fields["dry_run"].Flag = true;
        file.Save(d.Render(), () =>
        {
            string temporary = Assert.Single(Directory.GetFiles(f.Root, ".fileorganizer-config-*.tmp"));
            Assert.Throws<IOException>(() => File.Delete(temporary));
            Assert.Throws<IOException>(() => File.WriteAllText(temporary, "corrupt"));
            Assert.Throws<IOException>(() => Directory.Move(f.Root, f.Root + "-redirected"));
        });
        Assert.True(ConfigurationLoader.Load(f.ConfigPath).General.DryRun);
    }
    [WindowsFact]
    public void StartupReadReportsActualMismatchedAndFailedStates()
    {
        using Fixture f = new(); FakeStore store = new();
        string engine = Path.Combine(f.Root, "FileOrganizer.exe");
        Assert.Equal(StartupStatus.Absent, StartupRegistration.Read(store, engine, f.ConfigPath).Status);
        StartupRegistration.Install(store, engine, f.ConfigPath);
        Assert.Equal(StartupStatus.Registered, StartupRegistration.Read(store, engine, f.ConfigPath).Status);
        Assert.Equal(StartupStatus.Mismatched, StartupRegistration.Read(store, "other.exe", f.ConfigPath).Status);
        store.FailRead = true;
        Assert.Equal(StartupStatus.Unreadable, StartupRegistration.Read(store, engine, f.ConfigPath).Status);
        store.FailRead = false; StartupRegistration.Uninstall(store, f.ConfigPath);
        Assert.Equal(StartupStatus.Absent, StartupRegistration.Read(store, engine, f.ConfigPath).Status);
    }
    [WindowsFact]
    public void RealHkcuRegistrationRoundTripsInAnIsolatedKeyWithoutElevation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture f = new();
        string key = @"Software\FileOrganizer.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            RegistryStartupStore store = new(key);
            string engine = Path.Combine(f.Root, "folder with spaces", "FileOrganizer.exe");
            StartupRegistration.Install(store, engine, f.ConfigPath);
            Assert.Equal(StartupStatus.Registered, StartupRegistration.Read(store, engine, f.ConfigPath).Status);
            store.Set("unrelated", "keep"); StartupRegistration.Uninstall(store, f.ConfigPath);
            Assert.Null(store.Get(StartupRegistration.EntryName(f.ConfigPath))); Assert.Equal("keep", store.Get("unrelated"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }
    private sealed class FakeStore : IStartupStore
    {
        private readonly Dictionary<string, string> entries = [];
        public bool FailRead { get; set; }
        public string? Get(string name) => FailRead ? throw new UnauthorizedAccessException("denied") : entries.GetValueOrDefault(name);
        public void Set(string name, string command) => entries[name] = command;
        public void Remove(string name) => entries.Remove(name);
    }
}
