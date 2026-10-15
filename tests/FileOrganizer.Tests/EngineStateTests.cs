using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class EngineStateTests
{
    [WindowsFact]
    public void SnapshotRejectsStaleProcessIdentityAndMalformedData()
    {
        using Fixture f = new(); EngineStateStore states = new(Path.Combine(f.Root, "state"));
        EngineSnapshot snapshot = states.Create(f.ConfigPath, f.Text(), f.Config());
        states.Write(snapshot); Assert.NotNull(states.Read());
        states.Write(snapshot with { StartedUtcTicks = snapshot.StartedUtcTicks + 1 }); Assert.Null(states.Read());
        File.WriteAllText(states.FilePath, "{}"); Assert.Throws<IOException>(() => states.Read());
    }
    [WindowsFact]
    public async Task AcceptedAndRejectedReloadsReportTheActualAppliedSnapshot()
    {
        using Fixture f = new(); string text = f.Text(general: "scan_on_startup=false"); File.WriteAllText(f.ConfigPath, text);
        Configuration config = ConfigurationLoader.Parse(text); EngineStateStore states = new(Path.Combine(f.Root, "state"));
        EngineSnapshot snapshot = states.Create(f.ConfigPath, text, config); states.Write(snapshot);
        await using WorkerHost host = new(config, f.ConfigPath, f.Log(), accepted: (nextText, next) =>
            { snapshot = states.Create(f.ConfigPath, nextText, next); states.Write(snapshot); },
            rejected: error => { snapshot = snapshot with { ReloadError = error }; states.Write(snapshot); });
        using CancellationTokenSource stop = new(); Task running = host.RunAsync(stop.Token);
        try
        {
            ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open();
            d.Folders[0].Fields["recursive"].Flag = true; file.Save(d.Render());
            await Fixture.Eventually(() => host.Current.Folders[0].Recursive && states.Read()?.Folders[0].Recursive == true);
            string acceptedHash = states.Read()!.ConfigHash;
            Assert.Equal(EngineStateStore.Hash(File.ReadAllText(f.ConfigPath)), acceptedHash);
            File.WriteAllText(f.ConfigPath, "version=[broken");
            await Fixture.Eventually(() => states.Read()?.ReloadError is not null);
            Assert.Equal(acceptedHash, states.Read()!.ConfigHash); Assert.True(states.Read()!.Folders[0].Recursive);
        }
        finally { stop.Cancel(); await running; states.Clear(snapshot); }
        Assert.False(File.Exists(states.FilePath));
    }
}
