using System.IO;
using System.Windows.Input;
using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer.Configurator;

public sealed partial class EditorViewModel
{
    private Func<string, string, CancellationToken, Task<ScanResult>> scanRunner = null!;
    private Func<bool> workerRunning = null!, requestWorkerStop = null!;
    private CancellationTokenSource? scanCancellation;
    public ICommand StartScanCommand { get; private set; } = null!;
    public ICommand StopScanCommand { get; private set; } = null!;
    private void InitializeScan(Func<string, string, CancellationToken, Task<ScanResult>> runner, Func<bool> running, Func<bool> stop)
    {
        scanRunner = runner; workerRunning = running; requestWorkerStop = stop;
        StartScanCommand = new EditorCommand(async () => await StartScanAsync(), () => !Busy && file is not null && document is not null && !IsDirty && !external);
        StopScanCommand = new EditorCommand(async () => await StopScanAsync(), () => !Busy || scanCancellation is not null);
    }
    public async Task StartScanAsync()
    {
        if (Busy) return;
        if (file is null || document is null || IsDirty || external)
        { Status = "Salva una configurazione valida e risolvi eventuali conflitti prima di avviare la scansione."; return; }
        scanCancellation = new(); Busy = true;
        try
        {
            string path = file.Path, engine = enginePath;
            CancellationToken token = scanCancellation.Token;
            bool dryRun = await Task.Run(() =>
            {
                if (workerRunning()) throw new IOException("Il motore è già attivo. Premi Arresta scansione e attendi l'arresto prima di avviare una scansione singola.");
                if (file.HasExternalChanges()) throw new ConfigurationConflictException("Il TOML è cambiato: ricaricalo prima di avviare la scansione.");
                Configuration config = ConfigurationLoader.Parse(ConfigurationFile.ReadSharedText(path));
                if (!Path.IsPathFullyQualified(engine) || !File.Exists(engine) || !Path.GetFileName(engine).Equals("FileOrganizer.exe", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Seleziona il motore FileOrganizer.exe in General and Windows startup.");
                return config.General.DryRun;
            });
            token.ThrowIfCancellationRequested();
            Status = "Scansione in corso con il TOML salvato: " + path;
            ScanResult result = await scanRunner(engine, path, token);
            Status = result.ExitCode switch
            {
                0 => dryRun ? "Simulazione completata: nessun file spostato." : "Scansione completata. Consulta History per gli spostamenti registrati.",
                130 => "Scansione arrestata in sicurezza. Gli spostamenti già completati sono conservati.",
                2 => "Scansione non avviata: configurazione o comando non validi.",
                _ => "Scansione terminata con errori o motore già attivo. Consulta i log."
            };
            if (!string.IsNullOrWhiteSpace(result.Error)) Status += "\n" + result.Error;
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)) Status += "\n" + result.Output;
        }
        catch (OperationCanceledException) { Status = "Avvio scansione annullato."; }
        catch (Exception exception) when (Expected(exception)) { Status = "Scansione non avviata: " + exception.Message; }
        finally
        {
            try
            {
                await RefreshDiskAndRuntimeAsync();
                if (!disposed && (file is not null || showAllHistory))
                {
                    historyEntries = await Task.Run(() => history.Read(showAllHistory ? null : file?.Path));
                    selectedHistoryIds = []; historyStatus = $"{historyEntries.Count} movements in the last seven days.";
                    Notify(nameof(HistoryEntries)); Notify(nameof(HistoryStatus));
                }
            }
            catch (Exception exception) when (Expected(exception)) { Status += "\nCronologia non disponibile: " + exception.Message; }
            finally { scanCancellation.Dispose(); scanCancellation = null; Busy = false; }
        }
    }
    public async Task StopScanAsync()
    {
        if (scanCancellation is not null)
        {
            scanCancellation.Cancel();
            Status = "Arresto richiesto: attendo la conclusione sicura degli spostamenti in corso.";
            return;
        }
        if (Busy) return;
        try
        {
            bool requested = await Task.Run(requestWorkerStop);
            Status = requested ? "Arresto richiesto al motore in background. Attendi che lo stato risulti fermo prima di avviare una scansione." : "Nessuna scansione o motore attivo ha accettato la richiesta di arresto.";
            await RefreshDiskAndRuntimeAsync();
        }
        catch (Exception exception) when (Expected(exception)) { Status = "Impossibile richiedere l'arresto: " + exception.Message; }
    }
}
