using FileOrganizer.Core;
using FileOrganizer.Windows;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class TransferTests
{
    [WindowsTheory]
    [InlineData(false, "hello_2.txt")]
    [InlineData(true, "hello_copy_2.txt")]
    public async Task T12_T14_CollisionPreservesBytes(bool identical, string expected)
    {
        using Fixture f = new();
        Directory.CreateDirectory(f.Destination);
        File.WriteAllText(Path.Combine(f.Destination, "hello.txt"), identical ? "data" : "xxxx");
        File.WriteAllText(Path.Combine(f.Destination, identical ? "hello_copy_1.txt" : "hello_1.txt"), "keep");
        string source = f.File();
        Assert.Equal(TransferOutcome.Moved, await f.Move(f.Config(), source));
        Assert.False(File.Exists(source));
        Assert.Equal("data", File.ReadAllText(Path.Combine(f.Destination, expected)));
        Assert.Equal(identical ? "data" : "xxxx", File.ReadAllText(Path.Combine(f.Destination, "hello.txt")));
    }
    [WindowsTheory]
    [InlineData("identical_content='skip'", "data", TransferOutcome.Skipped)]
    [InlineData("name_collision='skip'", "xxxx", TransferOutcome.Skipped)]
    [InlineData("identical_content='delete'", "data", TransferOutcome.DeletedIdentical)]
    [InlineData("identical_content='delete'", "xxxx", TransferOutcome.Moved)]
    public async Task T15_ExplicitPolicies(string policy, string existing, TransferOutcome outcome)
    {
        using Fixture f = new();
        Directory.CreateDirectory(f.Destination);
        File.WriteAllText(Path.Combine(f.Destination, "hello.txt"), existing);
        string source = f.File();
        Assert.Equal(outcome, await f.Move(f.Config(policy), source));
        Assert.Equal(outcome == TransferOutcome.Skipped, File.Exists(source));
        Assert.Equal(existing, File.ReadAllText(Path.Combine(f.Destination, "hello.txt")));
    }
    [WindowsFact]
    public async Task T16_CommitRaceRetriesWithoutOverwrite()
    {
        using Fixture f = new();
        string source = f.File();
        int hooks = 0;
        await f.Move(f.Config(), source, hook: () =>
        { if (Interlocked.Increment(ref hooks) == 1) File.WriteAllText(Path.Combine(f.Destination, "hello.txt"), "other"); });
        Assert.Equal("other", File.ReadAllText(Path.Combine(f.Destination, "hello.txt")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(f.Destination, "hello_1.txt")));
    }
    [WindowsFact]
    public async Task T16_T17_ConcurrentTransfersPreserveAllFiles()
    {
        using Fixture f = new();
        Configuration config = f.Config(recursive: true);
        List<Task<TransferOutcome>> tasks = [];
        for (int i = 0; i < 8; i++)
        {
            string directory = Path.Combine(f.Source, i.ToString());
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "same.txt");
            File.WriteAllText(source, i.ToString());
            tasks.Add(Task.Run(() => f.Move(config, source)));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(8, Directory.GetFiles(f.Destination).Length);
        Assert.Equal(8, Directory.GetFiles(f.Destination).Select(File.ReadAllText).Distinct().Count());
    }
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T18_CopyTransactionVerificationAndCleanup(bool fail)
    {
        using Fixture f = new();
        string source = f.File(contents: new string('x', 200000));
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => f.Move(f.Config(), source, true, () => throw new IOException("injected failure")));
            Assert.True(File.Exists(source));
            Assert.Empty(Directory.GetFiles(f.Destination));
        }
        else
        {
            await f.Move(f.Config(), source, true);
            Assert.False(File.Exists(source));
            Assert.Equal(new string('x', 200000), File.ReadAllText(Path.Combine(f.Destination, "hello.txt")));
            Assert.Single(Directory.GetFiles(f.Destination));
        }
    }
    [WindowsFact]
    public async Task T19_LockedMissingAndDirectoryTargetsFailSafely()
    {
        using Fixture f = new();
        Configuration config = f.Config();
        string source = f.File();
        using (FileStream locked = new(source, FileMode.Open, FileAccess.Write, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => f.Move(config, source));
        Directory.CreateDirectory(Path.Combine(f.Destination, "hello.txt"));
        await Assert.ThrowsAsync<IOException>(() => f.Move(config, source));
        Assert.True(File.Exists(source));
        await Assert.ThrowsAsync<IOException>(() => f.Move(config, Path.Combine(f.Source, "missing.txt")));
    }
    [WindowsFact]
    public async Task T11_DestinationReparseSubstitutionRejected()
    {
        using Fixture f = new();
        Configuration config = f.Config();
        string source = f.File();
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe") { CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", f.Destination, f.Source }) start.ArgumentList.Add(arg);
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(start)!;
        p.WaitForExit(); Assert.Equal(0, p.ExitCode);
        try { await Assert.ThrowsAsync<IOException>(() => f.Move(config, source)); Assert.True(File.Exists(source)); }
        finally { Directory.Delete(f.Destination); }
    }
    [WindowsFact]
    public async Task T15_ConcurrentMutationBlocksVerifiedDeletion()
    {
        using Fixture f = new();
        Directory.CreateDirectory(f.Destination);
        string destination = Path.Combine(f.Destination, "hello.txt");
        File.WriteAllText(destination, "data");
        string source = f.File();
        await Assert.ThrowsAsync<IOException>(() => f.Move(f.Config("identical_content='delete'"), source,
            hook: () => File.WriteAllText(destination, "changed")));
        Assert.Equal("data", File.ReadAllText(source));
        Assert.Equal("data", File.ReadAllText(destination));
    }
    [WindowsFact]
    public async Task T19_PermissionDeniedPreservesSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture f = new();
        string source = f.File();
        FileInfo file = new(source);
        FileSecurity original = file.GetAccessControl();
        FileSecurity denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        file.SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => f.Move(f.Config(), source));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(Path.Combine(f.Destination, "hello.txt")));
        }
        finally
        {
            FileSecurity restore = new();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            file.SetAccessControl(restore);
        }
        Assert.Equal("data", File.ReadAllText(source));
    }
    [WindowsFact]
    public async Task T12_AlreadyNumberedUnicodeNameIsPreserved()
    {
        using Fixture f = new();
        Directory.CreateDirectory(f.Destination);
        string name = "日本語_1.txt";
        File.WriteAllText(Path.Combine(f.Destination, name), "other");
        await f.Move(f.Config(), f.File(name));
        Assert.Equal("data", File.ReadAllText(Path.Combine(f.Destination, "日本語_1_1.txt")));
    }
    [WindowsFact]
    public async Task T11_HardLinkedSourceFailsClosed()
    {
        using Fixture f = new();
        string source = f.File();
        string link = Path.Combine(f.Source, "alias.txt");
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe") { CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "/c", "mklink", "/H", link, source }) start.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        await Assert.ThrowsAsync<IOException>(() => f.Move(f.Config("identical_content='delete'"), source));
        Assert.Equal("data", File.ReadAllText(source)); Assert.Equal("data", File.ReadAllText(link));
    }
    [WindowsFact]
    public async Task T25_DryRunDoesNotCreateDestinationOrDelete()
    {
        using Fixture f = new();
        string source = f.File();
        Assert.Equal(TransferOutcome.DryRun, await f.Move(f.Config("identical_content='delete'", "dry_run=true"), source));
        Assert.False(Directory.Exists(f.Destination)); Assert.True(File.Exists(source));
    }
}
