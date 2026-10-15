using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class SecondVolumeFactAttribute : FactAttribute
{
    public SecondVolumeFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows";
        else if (Environment.GetEnvironmentVariable("FILEORGANIZER_TEST_SECOND_VOLUME") is null)
            Skip = "Set FILEORGANIZER_TEST_SECOND_VOLUME to a writable temporary location on another local volume";
    }
}
public sealed class CrossVolumeTests
{
    [SecondVolumeFact]
    public async Task T18_RealCrossVolumeCommit()
    {
        using Fixture f = new();
        string basePath = Paths.Normalize(Environment.GetEnvironmentVariable("FILEORGANIZER_TEST_SECOND_VOLUME")!);
        Assert.NotEqual(Path.GetPathRoot(f.Source), Path.GetPathRoot(basePath));
        string destination = Path.Combine(basePath, "FileOrganizer.Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string contents = new string('界', 100000);
            string source = f.File(contents: contents);
            Configuration config = ConfigurationLoader.Parse(f.Text().Replace(f.Destination, destination));
            Assert.Equal(TransferOutcome.Moved, await f.Move(config, source));
            Assert.False(File.Exists(source));
            Assert.Equal(contents, File.ReadAllText(Path.Combine(destination, "hello.txt")));
            Assert.Single(Directory.GetFiles(destination));
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(basePath), Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(destination)!));
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
    }
}
