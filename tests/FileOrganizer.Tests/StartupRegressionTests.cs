using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class StartupRegressionTests
{
    [WindowsTheory]
    [InlineData("MotionBlur50% B.txt")]
    [InlineData("literal%TEMP%.txt")]
    [InlineData("literal%MISSING_ORGANIZER_VARIABLE%.txt")]
    public async Task PercentInExistingFilenameIsLiteral(string name)
    {
        using Fixture f = new();
        string source = f.File(name);
        Assert.Equal(TransferOutcome.Moved, await f.Move(f.Config(), source));
        Assert.Equal("data", File.ReadAllText(Path.Combine(f.Destination, name)));
    }

    [WindowsFact]
    public async Task FileRemovedDuringReadinessIsNotReportedAsTimeout()
    {
        using Fixture f = new();
        string source = f.File();
        using FileStream writer = new(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using WorkerHost host = new(f.Config(), f.ConfigPath, f.Log());
        Task<int> scan = host.ScanAsync();
        await Task.Delay(100);
        File.Delete(source);
        Assert.Equal(0, await scan);
        Assert.Equal(0, host.Failures);
        Assert.False(Directory.Exists(f.Destination));
    }
}
